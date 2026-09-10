using System.Runtime.InteropServices;

namespace Glance.App;

/// <summary>Minimal Win32 tray icon for unpackaged WinUI.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly WndProc _proc;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly Action _capture;
    private bool _added;
    private const uint WmTray = 0x8001;
    private const uint WmCommand = 0x0111;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int IdShow = 1001;
    private const int IdCapture = 1002;
    private const int IdQuit = 1003;

    public TrayIconHost(Action show, Action quit, Action capture)
    {
        _show = show;
        _quit = quit;
        _capture = capture;
        _proc = WndProcImpl;
        var wc = new WNDCLASS
        {
            lpszClassName = "GlanceTrayHidden",
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
        };
        RegisterClass(ref wc);
        _hwnd = CreateWindowEx(0, "GlanceTrayHidden", "", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512)), // IDI_APPLICATION
            szTip = "Glance",
        };
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTray)
        {
            var mouse = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouse == WmLButtonUp)
                _show();
            else if (mouse == WmRButtonUp)
                ShowMenu();
        }
        else if (msg == WmCommand)
        {
            var id = wParam.ToInt32() & 0xFFFF;
            if (id == IdShow) _show();
            else if (id == IdCapture) _capture();
            else if (id == IdQuit) _quit();
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, IdShow, "显示窗口");
        AppendMenu(menu, 0, IdCapture, "截屏翻译");
        AppendMenu(menu, 0x800, 0, ""); // MF_SEPARATOR
        AppendMenu(menu, 0, IdQuit, "退出");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (!_added) return;
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
