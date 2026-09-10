using System.Diagnostics;
using System.Runtime.InteropServices;
using Glance.Capture;
using Glance.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Glance.App;

public sealed class AppServices
{
    public const string SilentStartArg = "--minimized";
    private const int HotkeyIdTranslate = 0x4701;
    private const int HotkeyIdCopy = 0x4702;
    private const int HotkeyIdPopup = 0x4703;

    public static AppServices Current { get; private set; } = null!;

    public ConfigStore Store { get; } = new();
    public YoudaoClient Youdao { get; } = new();
    public BingTranslateClient Bing { get; } = new();
    public CaptureService Capture { get; }

    public MainWindow? MainWindow { get; set; }
    public bool MainWindowShown { get; private set; }

    private readonly HotkeyWindow _hotkeys = new();
    private TrayIconHost? _tray;
    private Mutex? _singleInstance;

    public AppServices()
    {
        Capture = new CaptureService(Youdao, Store);
        Current = this;
    }

    public bool TryTakeSingleInstance()
    {
        _singleInstance = new Mutex(true, @"Local\Glance.WinUI.SingleInstance", out var created);
        return created;
    }

    public void ReleaseSingleInstance()
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* ignore */ }
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    public void InitializeShell(MainWindow window, bool startMinimized)
    {
        MainWindow = window;
        Store.Ensure();
        var settings = Store.LoadSettings();
        ApplyPin(settings.PinOnTop);
        ApplyAutostart(settings.Autostart);
        ReregisterHotkeys(settings);

        _tray = new TrayIconHost(
            show: ShowMainWindow,
            quit: Quit,
            capture: () => _ = BeginCaptureAsync(CaptureMode.Translate));

        window.Closed += (_, _) =>
        {
            // Close-to-tray handled in MainWindow; this is final exit.
            _hotkeys.UnregisterAll();
            _tray?.Dispose();
        };

        if (startMinimized)
        {
            HideMainWindow();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
        MainWindow.Activate();
        MainWindowShown = true;
    }

    public void HideMainWindow()
    {
        if (MainWindow is null) return;
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        ShowWindow(hwnd, 0); // SW_HIDE
        MainWindowShown = false;
    }

    public void ToggleMainWindow()
    {
        if (MainWindowShown) HideMainWindow();
        else ShowMainWindow();
    }

    public void ApplyPin(bool pinned)
    {
        if (MainWindow is null) return;
        var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(true, true);
        MainWindow.AppWindow.IsShownInSwitchers = true;
        // WinUI: use AppWindow AlwaysOnTop via OverlappedPresenter
        if (presenter is not null)
            presenter.IsAlwaysOnTop = pinned;
    }

    public void ApplyAutostart(bool enabled)
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe)) return;
        AutostartHelper.Apply(enabled, exe);
    }

    public void ReregisterHotkeys(TranslatorSettings settings)
    {
        _hotkeys.UnregisterAll();
        _hotkeys.EnsureCreated();
        _hotkeys.OnHotkey = id =>
        {
            MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                if (id == HotkeyIdTranslate) _ = BeginCaptureAsync(CaptureMode.Translate);
                else if (id == HotkeyIdCopy) _ = BeginCaptureAsync(CaptureMode.CopyText);
                else if (id == HotkeyIdPopup) ToggleMainWindow();
            });
        };

        if (HotkeyParser.TryParse(settings.Hotkey, out var t))
            _hotkeys.Register(HotkeyIdTranslate, t);
        if (HotkeyParser.TryParse(settings.CopyHotkey, out var c))
            _hotkeys.Register(HotkeyIdCopy, c);
        if (HotkeyParser.TryParse(settings.PopupShortcut, out var p))
            _hotkeys.Register(HotkeyIdPopup, p);
    }

    public async Task BeginCaptureAsync(CaptureMode mode)
    {
        // Do not restore main window after capture (parity with Tauri restore_main_window=false).
        HideMainWindow();
        try
        {
            await Capture.RunAsync(mode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("capture failed: " + ex);
        }
    }

    public void Quit()
    {
        _hotkeys.UnregisterAll();
        _tray?.Dispose();
        ReleaseSingleInstance();
        Application.Current.Exit();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
