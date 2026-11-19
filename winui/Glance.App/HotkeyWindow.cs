using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Glance.Core;

namespace Glance.App;

/// <summary>
/// Global hotkeys on a dedicated STA thread with its own message pump.
/// Avoids WinUI UI-thread / HWND_MESSAGE delivery quirks.
/// </summary>
internal sealed class HotkeyWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmAppWork = 0x8000 + 32;
    private const int WmQuitPump = 0x8000 + 34;
    private const int ErrorClassAlreadyExists = 1410;

    private IntPtr _hwnd;
    private Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private WndProc? _proc;
    private readonly HashSet<int> _ids = [];
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private Exception? _startError;

    public Action<int>? OnHotkey { get; set; }
    public int LastError { get; private set; }

    public void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;
        lock (_gate)
        {
            if (_hwnd != IntPtr.Zero) return;
            if (_thread is { IsAlive: true })
            {
                _ready.Wait(3000);
                return;
            }

            _ready.Reset();
            _startError = null;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "glance-hotkeys",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(5000))
                throw new TimeoutException("hotkey thread failed to start");
            if (_startError is not null)
                throw _startError;
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("hotkey HWND was not created");
        }
    }

    public bool Register(int id, HotkeySpec spec)
    {
        EnsureCreated();
        var ok = false;
        var err = 0;
        InvokeOnHotkeyThread(() =>
        {
            UnregisterHotKey(_hwnd, id);
            ok = RegisterHotKey(_hwnd, id, spec.Modifiers, spec.VirtualKey);
            if (!ok && (spec.Modifiers & HotkeySpec.ModNoRepeat) != 0)
                ok = RegisterHotKey(_hwnd, id, spec.Modifiers & ~HotkeySpec.ModNoRepeat, spec.VirtualKey);
            err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) _ids.Add(id);
            else _ids.Remove(id);
        });
        LastError = err;
        return ok;
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        InvokeOnHotkeyThread(() =>
        {
            foreach (var id in _ids.ToArray())
                UnregisterHotKey(_hwnd, id);
            _ids.Clear();
        });
    }

    public void Dispose()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
                PostMessage(_hwnd, WmQuitPump, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
        }
        catch { /* ignore */ }
        _ready.Dispose();
    }

    private void InvokeOnHotkeyThread(Action work)
    {
        if (Thread.CurrentThread == _thread)
        {
            work();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        _work.Enqueue(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        PostMessage(_hwnd, WmAppWork, IntPtr.Zero, IntPtr.Zero);
        if (!done.Wait(3000))
            throw new TimeoutException("hotkey thread did not process work");
        if (error is not null) throw error;
    }

    private void ThreadMain()
    {
        try
        {
            _proc = WndProcImpl;
            var wc = new WNDCLASS
            {
                lpszClassName = "GlanceHotkeyHidden",
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
            };
            var atom = RegisterClass(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorClassAlreadyExists)
                    throw new InvalidOperationException("RegisterClass failed: " + err);
            }

            _hwnd = CreateWindowEx(
                0, "GlanceHotkeyHidden", "", 0,
                0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmHotkey:
                try { OnHotkey?.Invoke(wParam.ToInt32()); }
                catch { /* never break the pump */ }
                return IntPtr.Zero;

            case WmAppWork:
                while (_work.TryDequeue(out var work))
                    work();
                return IntPtr.Zero;

            case WmQuitPump:
                while (_work.TryDequeue(out var work))
                {
                    try { work(); } catch { /* ignore */ }
                }
                DestroyWindow(hWnd);
                PostQuitMessage(0);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
