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
    public WordbookStore Wordbook { get; }
    public YoudaoClient Youdao { get; } = new();
    public YoudaoDictClient Dict { get; } = new();
    public TextTranslator Translator { get; } = new();
    public CaptureService Capture { get; }
    public AppUpdateService Updates { get; }

    public MainWindow? MainWindow { get; set; }
    public bool MainWindowShown { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<CaptureSessionResult>? CaptureCompleted;

    public void ReportStatus(string message)
        => MainWindow?.DispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(message));

    private SettingsWindow? _settingsWindow;
    private WordbookWindow? _wordbookWindow;
    private WordCaptureWindow? _wordCaptureWindow;
    private readonly WordCaptureService _wordCapture = new();
    private readonly HotkeyWindow _hotkeys = new();
    private TrayIconHost? _tray;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private volatile bool _activateStop;
    // Debug builds use a separate mutex so `dotnet run` / local exe does not
    // hand focus to the installed Velopack app under %LocalAppData%\Glance.
#if DEBUG
    private const string ActivateEventName = @"Local\Glance.WinUI.Activate.Debug";
    private const string SingleInstanceMutexName = @"Local\Glance.WinUI.SingleInstance.Debug";
#else
    private const string ActivateEventName = @"Local\Glance.WinUI.Activate";
    private const string SingleInstanceMutexName = @"Local\Glance.WinUI.SingleInstance";
#endif

    public AppServices()
    {
        Store.Ensure();
        Wordbook = new WordbookStore(Store.BaseDir);
        Capture = new CaptureService(Youdao, Store);
        var localUpdate = Environment.GetEnvironmentVariable("GLANCE_UPDATE_SOURCE");
        Updates = new AppUpdateService(localUpdate);
        Current = this;
    }

    public bool TryTakeSingleInstance()
    {
        _singleInstance = new Mutex(true, SingleInstanceMutexName, out var created);
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
        ApplyWordCapture(settings.WordCaptureEnabled);
        _wordCapture.SelectionCaptured += OnWordSelectionCaptured;

        _tray = new TrayIconHost(
            show: ShowMainWindow,
            quit: Quit,
            capture: () => _ = BeginCaptureAsync(CaptureMode.Translate));

        window.Closed += (_, _) =>
        {
            // Close-to-tray handled in MainWindow; this is final exit.
            _hotkeys.UnregisterAll();
            _wordCapture.Dispose();
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
        // Uncloak before show so the first painted frame is visible.
        SetWindowCloaked(hwnd, false);
        try { MainWindow.AppWindow.Show(); } catch { /* older path */ }
        // SW_SHOW (not SW_RESTORE): Restore re-applies a previous tall size and undoes our compact snap.
        ShowWindow(hwnd, 5); // SW_SHOW
        SetForegroundWindow(hwnd);
        MainWindow.Activate();
        // Snap after show so layout/DPI are real; enqueue once more after the first frame.
        if (MainWindow is MainWindow main)
        {
            main.SnapToContent();
            main.DispatcherQueue.TryEnqueue(() => main.SnapToContent());
        }
        MainWindowShown = true;
    }

    public void HideMainWindow()
    {
        if (MainWindow is null) return;
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        // Cloak removes the HWND from DWM composition immediately (Mica can
        // otherwise linger in CopyFromScreen for a frame or two after SW_HIDE).
        SetWindowCloaked(hwnd, true);
        try { MainWindow.AppWindow.Hide(); } catch { /* older path */ }
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

    public void ApplyWordCapture(bool enabled)
        => _wordCapture.SetEnabled(enabled);

    private void OnWordSelectionCaptured(string text, int screenX, int screenY)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            _wordCaptureWindow ??= new WordCaptureWindow();
            _wordCaptureWindow.ShowLookup(text, screenX, screenY);
        });
    }

    public void SuspendHotkeys() => _hotkeys.UnregisterAll();

    public void ReregisterHotkeys(TranslatorSettings settings)
        => _ = TryReregisterHotkeys(settings, out _);

    /// <returns>false if any chord failed to register; <paramref name="failed"/> lists labels.</returns>
    public bool TryReregisterHotkeys(TranslatorSettings settings, out List<string> failed)
    {
        failed = [];
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

        if (!string.IsNullOrWhiteSpace(settings.Hotkey))
        {
            var raw = HotkeyParser.Canonicalize(settings.Hotkey) ?? settings.Hotkey;
            if (!HotkeyParser.TryParse(raw, out var t) || !_hotkeys.Register(HotkeyIdTranslate, t))
                failed.Add($"截屏翻译({raw})");
            else
                settings.Hotkey = raw;
        }
        if (!string.IsNullOrWhiteSpace(settings.CopyHotkey))
        {
            var raw = HotkeyParser.Canonicalize(settings.CopyHotkey) ?? settings.CopyHotkey;
            if (!HotkeyParser.TryParse(raw, out var c) || !_hotkeys.Register(HotkeyIdCopy, c))
                failed.Add($"OCR 复制({raw})");
            else
                settings.CopyHotkey = raw;
        }
        if (!string.IsNullOrWhiteSpace(settings.PopupShortcut))
        {
            var raw = HotkeyParser.Canonicalize(settings.PopupShortcut) ?? settings.PopupShortcut;
            if (!HotkeyParser.TryParse(raw, out var p) || !_hotkeys.Register(HotkeyIdPopup, p))
                failed.Add($"弹出主窗({raw})");
            else
                settings.PopupShortcut = raw;
        }
        if (failed.Count > 0)
            ReportStatus("热键注册失败: " + string.Join("、", failed) + "（可能被其他软件占用）");
        return failed.Count == 0;
    }

    public async Task<CaptureSessionResult> BeginCaptureAsync(CaptureMode mode)
    {
        if (Capture.IsBusy)
        {
            ReportStatus("已有截屏任务进行中");
            return new CaptureSessionResult { Cancelled = true };
        }

        // Drop hotkey-recording so RegisterHotKey is armed again after capture.
        CancelTransientUi();

        ReportStatus(mode == CaptureMode.CopyText ? "识别中…" : "截屏翻译中…");
        var wasShown = MainWindowShown;
        var showMainAfter = wasShown;
        var pinned = false;
        try
        {
            pinned = Store.LoadSettings().PinOnTop;
        }
        catch { /* ignore */ }

        // Drop AlwaysOnTop before hide so a pinned window cannot race above the overlay.
        if (pinned) ApplyPin(false);
        HideMainWindow();
        await WaitUntilMainWindowGoneAsync();
        try
        {
            var result = await Capture.RunAsync(mode);
            // CopiedText is set for OCR-copy mode and overlay "send to main" (may be empty string).
            var sentToMain = result.CopiedText is not null;
            if (sentToMain)
            {
                ReportStatus(string.IsNullOrWhiteSpace(result.CopiedText)
                    ? "已打开主窗口（未识别到原文）"
                    : "已送入主窗口（可点朗读）");
                showMainAfter = true; // even if capture started from tray/hotkey
            }
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
            if (showMainAfter)
                ShowMainWindow();
            // Restore pin even when the window stayed hidden (hotkey-only capture).
            if (pinned)
                ApplyPin(true);
        }
    }

    public void CancelTransientUi()
    {
        if (MainWindow is MainWindow win)
            win.CancelTransientUi();
        _settingsWindow?.CancelTransientUi();
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            ApplyToolWindowThemes(Store.LoadSettings().UiTheme);
        }
        ToolWindow.Activate(_settingsWindow);
    }

    public void ShowWordbookWindow()
    {
        if (_wordbookWindow is null)
        {
            _wordbookWindow = new WordbookWindow();
            _wordbookWindow.Closed += (_, _) => _wordbookWindow = null;
            ApplyToolWindowThemes(Store.LoadSettings().UiTheme);
        }
        _wordbookWindow.Refresh();
        ToolWindow.Activate(_wordbookWindow);
    }

    public void RefreshWordbookWindow() => _wordbookWindow?.Refresh();

    public void ApplyToolWindowThemes(string? theme)
    {
        ToolWindow.ApplyTheme(_settingsWindow, theme);
        ToolWindow.ApplyTheme(_wordbookWindow, theme);
    }

    public void Quit()
    {
        _hotkeys.UnregisterAll();
        _wordCapture.Dispose();
        _tray?.Dispose();
        ReleaseSingleInstance();
        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        try { _wordbookWindow?.Close(); } catch { /* ignore */ }
        try { _wordCaptureWindow?.Close(); } catch { /* ignore */ }
        if (MainWindow is MainWindow win)
            win.AllowClose();
        Application.Current.Exit();
    }

    /// <summary>
    /// Wait until the main HWND is no longer visible, then give DWM a short
    /// settle so Mica composition is gone before CopyFromScreen.
    /// </summary>
    private async Task WaitUntilMainWindowGoneAsync()
    {
        if (MainWindow is null) return;
        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        var deadline = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < deadline)
        {
            if (!IsWindowVisible(hwnd) && !MainWindow.AppWindow.IsVisible)
            {
                // One short settle after cloak/hide; avoid a fixed 50ms tax every capture.
                await Task.Delay(16);
                return;
            }
            await Task.Delay(16);
        }
    }

    private static void SetWindowCloaked(IntPtr hwnd, bool cloak)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = cloak ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
    }

    private const int DwmwaCloak = 13;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
