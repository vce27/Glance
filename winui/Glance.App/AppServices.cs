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
    public TextTranslator Translator { get; } = new();
    public CaptureService Capture { get; }
    public AppUpdateService Updates { get; }

    public MainWindow? MainWindow { get; set; }
    public bool MainWindowShown { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<CaptureSessionResult>? CaptureCompleted;

    public void ReportStatus(string message)
        => MainWindow?.DispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(message));

    private readonly HotkeyWindow _hotkeys = new();
    private TrayIconHost? _tray;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private volatile bool _activateStop;
    private const string ActivateEventName = @"Local\Glance.WinUI.Activate";

    public AppServices()
    {
        Capture = new CaptureService(Youdao, Store);
        var localUpdate = Environment.GetEnvironmentVariable("GLANCE_UPDATE_SOURCE");
        Updates = new AppUpdateService(localUpdate);
        Current = this;
    }

    public bool TryTakeSingleInstance()
    {
        _singleInstance = new Mutex(true, @"Local\Glance.WinUI.SingleInstance", out var created);
        if (!created) return false;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(ActivateLoop)
        {
            IsBackground = true,
            Name = "glance-activate",
        };
        thread.Start();
        return true;
    }

    public static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
            ev.Set();
        }
        catch { /* first instance not listening yet */ }
    }

    private void ActivateLoop()
    {
        while (!_activateStop && _activateEvent is not null)
        {
            if (_activateEvent.WaitOne(400))
                MainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
        }
    }

    public void ReleaseSingleInstance()
    {
        _activateStop = true;
        try { _activateEvent?.Set(); } catch { /* ignore */ }
        _activateEvent?.Dispose();
        _activateEvent = null;
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

        var failed = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Hotkey))
        {
            if (!HotkeyParser.TryParse(settings.Hotkey, out var t) || !_hotkeys.Register(HotkeyIdTranslate, t))
                failed.Add("截屏翻译");
        }
        if (!string.IsNullOrWhiteSpace(settings.CopyHotkey))
        {
            if (!HotkeyParser.TryParse(settings.CopyHotkey, out var c) || !_hotkeys.Register(HotkeyIdCopy, c))
                failed.Add("OCR 复制");
        }
        if (!string.IsNullOrWhiteSpace(settings.PopupShortcut))
        {
            if (!HotkeyParser.TryParse(settings.PopupShortcut, out var p) || !_hotkeys.Register(HotkeyIdPopup, p))
                failed.Add("弹出主窗");
        }
        if (failed.Count > 0)
            ReportStatus("热键注册失败: " + string.Join("、", failed));
    }

    public async Task<CaptureSessionResult> BeginCaptureAsync(CaptureMode mode)
    {
        if (Capture.IsBusy)
        {
            ReportStatus("已有截屏任务进行中");
            return new CaptureSessionResult { Cancelled = true };
        }

        ReportStatus(mode == CaptureMode.CopyText ? "识别中…" : "截屏翻译中…");
        var wasShown = MainWindowShown;
        var pinned = false;
        try
        {
            pinned = Store.LoadSettings().PinOnTop;
        }
        catch { /* ignore */ }

        HideMainWindow();
        try
        {
            var result = await Capture.RunAsync(mode);
            if (!string.IsNullOrWhiteSpace(result.CopiedText))
                ReportStatus("已复制识别原文");
            else if (result.Translation is not null)
                ReportStatus("截屏翻译完成");
            else if (result.Cancelled)
                ReportStatus("");
            MainWindow?.DispatcherQueue.TryEnqueue(() => CaptureCompleted?.Invoke(result));
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("capture failed: " + ex);
            ReportStatus("截屏失败: " + ex.Message);
            return new CaptureSessionResult { Cancelled = true };
        }
        finally
        {
            if (wasShown)
            {
                ShowMainWindow();
                ApplyPin(pinned);
            }
        }
    }

    public void Quit()
    {
        _hotkeys.UnregisterAll();
        _tray?.Dispose();
        ReleaseSingleInstance();
        if (MainWindow is MainWindow win)
            win.AllowClose();
        Application.Current.Exit();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
