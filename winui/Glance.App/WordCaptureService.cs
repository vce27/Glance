using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Glance.App;

/// <summary>
/// Global select-to-lookup.
/// Policy (有道-style): never synthesize Ctrl+C unless the host is a known browser.
/// Native edits use WM_COPY; Chromium terminals/IDEs (Termark/Cursor/…) never get keystrokes.
/// </summary>
internal sealed class WordCaptureService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmCopy = 0x0301;
    private const int WmQuit = 0x0012;
    private const int WmAppCapture = 0x8001; // WM_APP+1 — run capture on the STA hook thread
    private const int GaRoot = 2;
    private const int MinDragPx = 8;
    private const int MaxChars = 60;
    private const int FireCooldownMs = 200;

    /// <summary>
    /// Ctrl+C is only safe for real browsers. Electron terminals share Chrome_WidgetWin_1
    /// with Chrome — denylisting Termark/Warp forever cannot scale; allowlist browsers instead.
    /// </summary>
    private static readonly HashSet<string> CtrlCSafeBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "brave-browser",
        "opera", "opera_browser", "vivaldi", "chromium", "arc",
        "firefox", "firefox-esr", "waterfox", "librewolf", "zen",
        "qqbrowser", "360chrome", "360se", "sogouexplorer", "maxthon",
        "dragon", "yandex", "avastbrowser", "island", "dia",
    };

    private static readonly Regex WordLike = new(
        @"^[\p{L}\p{M}\p{N}'’.\-]+(?:\s+[\p{L}\p{M}\p{N}'’.\-]+){0,5}$",
        RegexOptions.Compiled);

    private readonly object _gate = new();
    private IntPtr _hook;
    private HookProc? _proc;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _enabled;
    private volatile bool _stop;
    private int _downX, _downY;
    private int _dragX0, _dragY0;
    private bool _down;
    private long _lastFireMs;
    private int _captureSeq;

    public event Action<string, int, int>? SelectionCaptured;

    /// <summary>Any left-button down while capture is enabled (for dismissing the popup).</summary>
    public event Action<int, int>? PointerDown;

    /// <summary>
    /// Fallback when clipboard/UIA cannot read selection (terminals).
    /// Args: drag start/end screen coords → OCR text or null.
    /// </summary>
    public Func<int, int, int, int, Task<string?>>? TryOcrRegionAsync { get; set; }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) EnsureStarted();
        else Stop();
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_hook != IntPtr.Zero) return;
            _stop = false;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "glance-word-capture",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void Stop()
    {
        _stop = true;
        _enabled = false;
        uint tid = 0;
        lock (_gate) tid = _threadId;
        if (tid != 0)
            PostThreadMessage((int)tid, WmQuit, IntPtr.Zero, IntPtr.Zero);
        try { _thread?.Join(1500); } catch { /* ignore */ }
        lock (_gate)
        {
            _thread = null;
            _hook = IntPtr.Zero;
            _threadId = 0;
        }
    }

    public void Dispose() => Stop();

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        try
        {
            while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WmQuit) break;
                if (msg.message == WmAppCapture)
                {
                    TryCapture(unchecked((int)msg.wParam.ToInt64()), unchecked((int)msg.lParam.ToInt64()));
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _threadId = 0;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _enabled)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            if (msg == WmLButtonDown)
            {
                _down = true;
                _downX = info.pt.X;
                _downY = info.pt.Y;
                // Invalidate in-flight OCR so a late result cannot reopen a dismissed popup.
                Interlocked.Increment(ref _captureSeq);
                try { PointerDown?.Invoke(info.pt.X, info.pt.Y); } catch { /* ignore */ }
            }
            else if (msg == WmLButtonUp && _down)
            {
                _down = false;
                var dx = Math.Abs(info.pt.X - _downX);
                var dy = Math.Abs(info.pt.Y - _downY);
                if (dx >= MinDragPx || dy >= MinDragPx)
                {
                    _dragX0 = _downX;
                    _dragY0 = _downY;
                    // UIA COM requires STA — post back to this hook thread (not the MTA pool).
                    PostThreadMessage(
                        unchecked((int)_threadId), WmAppCapture, (IntPtr)info.pt.X, (IntPtr)info.pt.Y);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void TryCapture(int screenX, int screenY)
    {
        var now = Environment.TickCount64;
        if (now - _lastFireMs < FireCooldownMs) return;
        _lastFireMs = now;

        try
        {
            var fg = GetForegroundWindow();
            var atPoint = WindowFromPoint(new Point { X = screenX, Y = screenY });
            var rootAtPoint = atPoint != IntPtr.Zero ? GetAncestor(atPoint, GaRoot) : IntPtr.Zero;
            foreach (var hwnd in new[] { fg, rootAtPoint, atPoint })
            {
                if (hwnd == IntPtr.Zero) continue;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == (uint)Environment.ProcessId)
                    return;
            }

            // Allowlist: Ctrl+C only for known browsers. Unknown Electron (Termark etc.) → no keys.
            var allowCtrlC = CanUseCtrlC(fg, rootAtPoint);
            var chromium = IsChromiumShell(fg) || IsChromiumShell(rootAtPoint);

            string? text = null;

            if (!chromium)
            {
                // Native Edit/RichEdit — WM_COPY first, no keystrokes.
                text = TryCopyViaWmCopy(atPoint);
            }

            if (string.IsNullOrWhiteSpace(text))
                text = UiaSelectionReader.TryRead(screenX, screenY);

            if (string.IsNullOrWhiteSpace(text) && allowCtrlC)
                text = StealSelectionText();

            if (!string.IsNullOrWhiteSpace(text))
            {
                FireIfValid(text, screenX, screenY);
                return;
            }

            // Terminals / Electron shells: no Ctrl+C — OCR the drag band (有道划词同思路).
            if (TryOcrRegionAsync is null) return;
            var x0 = _dragX0;
            var y0 = _dragY0;
            var x1 = screenX;
            var y1 = screenY;
            var seq = Interlocked.Increment(ref _captureSeq);
            _ = Task.Run(async () =>
            {
                try
                {
                    var ocr = await TryOcrRegionAsync(x0, y0, x1, y1).ConfigureAwait(false);
                    if (seq != Volatile.Read(ref _captureSeq)) return;
                    if (string.IsNullOrWhiteSpace(ocr)) return;
                    FireIfValid(ocr, x1, y1);
                }
                catch
                {
                    // Ignore OCR failures.
                }
            });
        }
        catch
        {
            // Ignore capture failures.
        }
    }

    private void FireIfValid(string text, int screenX, int screenY)
    {
        text = Normalize(text);
        if (text.Length is 0 or > MaxChars) return;
        // OCR may return a short phrase — take the first token if the whole line fails WordLike.
        if (!WordLike.IsMatch(text) && !LooksChinese(text))
        {
            var token = text.Split([' ', '\t', '|', '｜', '·'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (token is null) return;
            text = token.Trim();
            if (text.Length is 0 or > MaxChars) return;
            if (!WordLike.IsMatch(text) && !LooksChinese(text)) return;
        }

        SelectionCaptured?.Invoke(text, screenX, screenY);
    }

    private static string Normalize(string text)
    {
        text = text.Trim().Replace("\r\n", " ").Replace('\n', ' ').Replace('\t', ' ');
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text;
    }

    private static bool LooksChinese(string text)
    {
        foreach (var ch in text)
            if (ch is >= '\u4e00' and <= '\u9fff') return true;
        return false;
    }

    /// <summary>
    /// Ctrl+C only when we know the host treats it as "copy", not SIGINT.
    /// Chromium/Electron shells (same HWND class as Chrome) require a browser allowlist.
    /// Classic consoles never get Ctrl+C.
    /// </summary>
    private static bool CanUseCtrlC(IntPtr fg, IntPtr rootAtPoint)
    {
        foreach (var hwnd in new[] { fg, rootAtPoint })
        {
            if (hwnd == IntPtr.Zero) continue;
            if (IsTerminalWindowClass(hwnd)) return false;
        }

        var shell = fg != IntPtr.Zero ? fg : rootAtPoint;
        if (IsChromiumShell(shell) || IsChromiumShell(rootAtPoint))
            return IsKnownBrowserProcess(shell != IntPtr.Zero ? shell : rootAtPoint);

        // Native non-console apps (Notepad, Word, …): Ctrl+C is copy.
        return true;
    }

    private static bool IsKnownBrowserProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return CtrlCSafeBrowsers.Contains(proc.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminalWindowClass(IntPtr hwnd)
    {
        var cls = new StringBuilder(256);
        if (GetClassName(hwnd, cls, cls.Capacity) <= 0) return false;
        var name = cls.ToString();
        return name.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Console", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Terminal", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("ConEmu", StringComparison.OrdinalIgnoreCase)
               || name.Equals("mintty", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChromiumShell(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = new StringBuilder(256);
        if (GetClassName(hwnd, cls, cls.Capacity) <= 0) return false;
        var name = cls.ToString();
        return name.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Chrome_WidgetWin_0", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Electron_BrowserWnd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ask the control under the cursor to copy selection via WM_COPY — does not
    /// synthesize Ctrl+C, so consoles / IDE terminals are never interrupted.
    /// </summary>
    private static string? TryCopyViaWmCopy(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        string? previous = null;
        if (OpenClipboard(IntPtr.Zero))
        {
            try { previous = ReadUnicodeClipboardUnlocked(); }
            finally { CloseClipboard(); }
        }

        // Keep this short — failing hosts should fall through to Ctrl+C quickly.
        for (var hop = 0; hop < 3 && hwnd != IntPtr.Zero; hop++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { EmptyClipboard(); }
                finally { CloseClipboard(); }
            }

            SendMessage(hwnd, WmCopy, IntPtr.Zero, IntPtr.Zero);

            for (var i = 0; i < 4; i++)
            {
                Thread.Sleep(8);
                if (!OpenClipboard(IntPtr.Zero)) continue;
                try
                {
                    var stolen = ReadUnicodeClipboardUnlocked();
                    if (!string.IsNullOrEmpty(stolen) && stolen != previous)
                        return stolen;
                }
                finally { CloseClipboard(); }
            }

            hwnd = GetParent(hwnd);
        }

        return null;
    }

    private static string? StealSelectionText()
    {
        string? previous = null;
        if (OpenClipboard(IntPtr.Zero))
        {
            try { previous = ReadUnicodeClipboardUnlocked(); }
            finally { CloseClipboard(); }
        }

        if (OpenClipboard(IntPtr.Zero))
        {
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
        }

        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x43, 0, 0, UIntPtr.Zero);
        keybd_event(0x43, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);

        string? stolen = null;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(8);
            if (!OpenClipboard(IntPtr.Zero)) continue;
            try
            {
                stolen = ReadUnicodeClipboardUnlocked();
                if (!string.IsNullOrEmpty(stolen) && stolen != previous)
                    break;
                stolen = null;
            }
            finally { CloseClipboard(); }
        }

        if (previous is not null && OpenClipboard(IntPtr.Zero))
        {
            try
            {
                EmptyClipboard();
                var bytes = Encoding.Unicode.GetBytes(previous + "\0");
                var h = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, (UIntPtr)bytes.Length);
                if (h != IntPtr.Zero)
                {
                    var p = GlobalLock(h);
                    if (p != IntPtr.Zero)
                    {
                        Marshal.Copy(bytes, 0, p, bytes.Length);
                        GlobalUnlock(h);
                        if (SetClipboardData(13, h) == IntPtr.Zero)
                            GlobalFree(h);
                    }
                    else GlobalFree(h);
                }
            }
            finally { CloseClipboard(); }
        }

        return stolen;
    }

    private static string? ReadUnicodeClipboardUnlocked()
    {
        var h = GetClipboardData(13);
        if (h == IntPtr.Zero) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { GlobalUnlock(h); }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point pt;
        public int mouseData;
        public int flags;
        public int time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
