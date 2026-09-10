using System.Runtime.InteropServices;
using Glance.Core;

namespace Glance.App;

/// <summary>Hidden message-only HWND that receives WM_HOTKEY.</summary>
internal sealed class HotkeyWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    private IntPtr _hwnd;
    private WndProc? _proc;
    private readonly HashSet<int> _ids = [];

    public Action<int>? OnHotkey { get; set; }

    public void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;
        _proc = WndProcImpl;
        var wc = new WNDCLASS
        {
            lpszClassName = "GlanceHotkeyHidden",
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
        };
        RegisterClass(ref wc);
        _hwnd = CreateWindowEx(0, "GlanceHotkeyHidden", "", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero); // HWND_MESSAGE = -3
    }

    public bool Register(int id, HotkeySpec spec)
    {
        EnsureCreated();
        UnregisterHotKey(_hwnd, id);
        var ok = RegisterHotKey(_hwnd, id, spec.Modifiers, spec.VirtualKey);
        if (ok) _ids.Add(id);
        return ok;
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in _ids.ToArray())
            UnregisterHotKey(_hwnd, id);
        _ids.Clear();
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey)
            OnHotkey?.Invoke(wParam.ToInt32());
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => UnregisterAll();

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
