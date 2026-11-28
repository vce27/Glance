using System.Runtime.InteropServices;
using Glance.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glance.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppServices _services = AppServices.Current;
    private TranslatorSettings _settings = new();
    private bool _loadingUi;
    private Button? _hotkeyRecordingButton;
    private string? _hotkeyRecordingPrevious;
    private DispatcherTimer? _hotkeyPollTimer;
    private readonly HashSet<int> _hotkeyPollSeen = [];
    private const string HotkeyEmptyLabel = "点击设置快捷键";
    private const string HotkeyRecordingLabel = "按下快捷键…";

    public SettingsWindow()
    {
        InitializeComponent();
        SetTitleBar(AppTitleBar);
        ToolWindow.Configure(this, "设置", 440, 640);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                ReloadFromStore();
        };
        Closed += (_, _) => CancelHotkeyRecording(restore: true);
        _services.Capture.ComparePreferenceChanged += show => DispatcherQueue.TryEnqueue(() =>
        {
            if (_loadingUi || CompareOverlaySwitch.IsOn == show) return;
            _loadingUi = true;
            CompareOverlaySwitch.IsOn = show;
            _loadingUi = false;
        });
        ReloadFromStore();
    }

    public void CancelTransientUi() => CancelHotkeyRecording(restore: true);

    private void ReloadFromStore()
    {
        _loadingUi = true;
        _settings = _services.Store.LoadSettings();
        AutostartSwitch.IsOn = _settings.Autostart;
        CompareOverlaySwitch.IsOn = _settings.ShowCompareOverlay;
        WordCaptureSwitch.IsOn = _settings.WordCaptureEnabled;
        AutoUpdateSwitch.IsOn = _settings.AutoCheckUpdates;
        SetHotkeyButton(HotkeyButton, HotkeyParser.Canonicalize(_settings.Hotkey));
        SetHotkeyButton(CopyHotkeyButton, HotkeyParser.Canonicalize(_settings.CopyHotkey));
        SetHotkeyButton(PopupHotkeyButton, HotkeyParser.Canonicalize(_settings.PopupShortcut));
        CustomProxyBox.Text = _settings.CustomProxy;
        LlmBaseUrlBox.Text = _settings.LlmConfig.BaseUrl;
        LlmApiKeyBox.Password = _settings.LlmConfig.ApiKey;
        LlmModelBox.Text = _settings.LlmConfig.Model;
        if (_settings.TextTranslateEngine == TextTranslateEngine.Llm)
            EngineLlm.IsChecked = true;
        else if (_settings.TextTranslateEngine == TextTranslateEngine.Offline)
            EngineOffline.IsChecked = true;
        else
            EngineBing.IsChecked = true;
        ApplyProxyUi();
        LlmPanel.Visibility = _settings.TextTranslateEngine == TextTranslateEngine.Llm
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshOfflinePackUi();
        UpdateStatusText.Text = $"当前版本 {_services.Updates.CurrentVersionDisplay}";
        ApplyUpdateButton.Visibility = Visibility.Collapsed;
        _loadingUi = false;
    }

    private void RefreshOfflinePackUi()
    {
        var pack = _services.Translator.Offline.Pack;
        var installed = pack.IsInstalled();
        OfflinePackStatusText.Text = installed
            ? "语言包已安装 · 断网自动启用"
            : "未安装语言包（可选，约 390MB）";
        OfflinePackDownloadButton.Content = installed ? "重新下载语言包" : "下载英↔中语言包";
        OfflinePackDeleteButton.IsEnabled = installed;
        OfflinePackProgress.Visibility = Visibility.Collapsed;
    }

    private void ApplyProxyUi()
    {
        switch (_settings.ProxyMode)
        {
            case ProxyMode.Custom:
                ProxyCustom.IsChecked = true;
                break;
            case ProxyMode.None:
                ProxyNone.IsChecked = true;
                break;
            default:
                ProxySystem.IsChecked = true;
                break;
        }
        CustomProxyBox.Visibility = _settings.ProxyMode == ProxyMode.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLocalStatus(string? msg) => StatusText.Text = msg ?? "";

    private void Persist()
    {
        if (_loadingUi) return;

        // Merge onto latest disk settings so main-window lang/pin edits aren't clobbered.
        var prev = _services.Store.LoadSettings();
        var prevHotkey = prev.Hotkey;
        var prevCopy = prev.CopyHotkey;
        var prevPopup = prev.PopupShortcut;

        prev.Autostart = AutostartSwitch.IsOn;
        prev.ShowCompareOverlay = CompareOverlaySwitch.IsOn;
        prev.WordCaptureEnabled = WordCaptureSwitch.IsOn;
        prev.CustomProxy = CustomProxyBox.Text.Trim();
        prev.LlmConfig.BaseUrl = LlmBaseUrlBox.Text.Trim();
        prev.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        prev.LlmConfig.Model = LlmModelBox.Text.Trim();
        prev.AutoCheckUpdates = AutoUpdateSwitch.IsOn;
        prev.TextTranslateEngine = EngineLlm.IsChecked == true
            ? TextTranslateEngine.Llm
            : EngineOffline.IsChecked == true
                ? TextTranslateEngine.Offline
                : TextTranslateEngine.Bing;
        if (ProxyCustom.IsChecked == true) prev.ProxyMode = ProxyMode.Custom;
        else if (ProxyNone.IsChecked == true) prev.ProxyMode = ProxyMode.None;
        else prev.ProxyMode = ProxyMode.System;

        _settings = prev;

        if (_hotkeyRecordingButton is not null)
        {
            _services.Store.SaveSettings(_settings);
            _services.ApplyAutostart(_settings.Autostart);
            return;
        }

        var nextHotkey = ReadHotkeyButton(HotkeyButton) ?? "";
        var nextCopy = ReadHotkeyButton(CopyHotkeyButton) ?? "";
        var nextPopup = ReadHotkeyButton(PopupHotkeyButton);

        if (HasHotkeyConflict(nextHotkey, nextCopy, nextPopup))
        {
            SetLocalStatus("热键冲突：同一快捷键不能用于多项，请改其中一个");
            SetHotkeyButton(HotkeyButton, string.IsNullOrEmpty(prevHotkey) ? null : prevHotkey);
            SetHotkeyButton(CopyHotkeyButton, string.IsNullOrEmpty(prevCopy) ? null : prevCopy);
            SetHotkeyButton(PopupHotkeyButton, prevPopup);
            _settings.Hotkey = prevHotkey;
            _settings.CopyHotkey = prevCopy;
            _settings.PopupShortcut = prevPopup;
            _services.Store.SaveSettings(_settings);
            _services.ApplyAutostart(_settings.Autostart);
            _services.ReregisterHotkeys(_settings);
            return;
        }

        _settings.Hotkey = nextHotkey;
        _settings.CopyHotkey = nextCopy;
        _settings.PopupShortcut = nextPopup;
        _services.ApplyAutostart(_settings.Autostart);

        if (!_services.TryReregisterHotkeys(_settings, out var failed) && failed.Count > 0)
        {
            if (failed.Exists(f => f.StartsWith("截屏翻译", StringComparison.Ordinal)))
            {
                _settings.Hotkey = prevHotkey;
                SetHotkeyButton(HotkeyButton, string.IsNullOrEmpty(prevHotkey) ? null : prevHotkey);
            }
            if (failed.Exists(f => f.StartsWith("OCR 复制", StringComparison.Ordinal)))
            {
                _settings.CopyHotkey = prevCopy;
                SetHotkeyButton(CopyHotkeyButton, string.IsNullOrEmpty(prevCopy) ? null : prevCopy);
            }
            if (failed.Exists(f => f.StartsWith("弹出主窗", StringComparison.Ordinal)))
            {
                _settings.PopupShortcut = prevPopup;
                SetHotkeyButton(PopupHotkeyButton, prevPopup);
            }
            _services.ReregisterHotkeys(_settings);
        }

        _services.Store.SaveSettings(_settings);
    }

    private static bool HasHotkeyConflict(string? a, string? b, string? c)
    {
        static string? Norm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return HotkeyParser.Canonicalize(s)?.ToUpperInvariant() ?? s.Trim().ToUpperInvariant();
        }
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in new[] { Norm(a), Norm(b), Norm(c) })
        {
            if (item is null) continue;
            if (!set.Add(item)) return true;
        }
        return false;
    }

    private void OnEngineChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        LlmPanel.Visibility = EngineLlm.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Persist();
    }

    private async void OnOfflinePackDownloadClick(object sender, RoutedEventArgs e)
    {
        OfflinePackDownloadButton.IsEnabled = false;
        OfflinePackDeleteButton.IsEnabled = false;
        OfflinePackProgress.Visibility = Visibility.Visible;
        OfflinePackProgress.Value = 0;
        OfflinePackStatusText.Text = "正在下载语言包…";
        try
        {
            var progress = new Progress<OfflineMtProgress>(p =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    OfflinePackProgress.Value = p.Percent;
                    OfflinePackStatusText.Text = p.Message;
                }));
            await _services.Translator.Offline.Pack.InstallAsync(null, progress);
            SetLocalStatus("离线语言包已安装");
        }
        catch (Exception ex)
        {
            OfflinePackStatusText.Text = "下载失败: " + ex.Message;
            SetLocalStatus("语言包下载失败");
        }
        finally
        {
            OfflinePackDownloadButton.IsEnabled = true;
            RefreshOfflinePackUi();
        }
    }

    private void OnOfflinePackDeleteClick(object sender, RoutedEventArgs e)
    {
        _services.Translator.Offline.Pack.Uninstall();
        _services.Translator.ResetOffline();
        RefreshOfflinePackUi();
        SetLocalStatus("已删除离线语言包");
    }

    private void OnProxyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        CustomProxyBox.Visibility = ProxyCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Persist();
    }

    private void OnFieldCommit(object sender, RoutedEventArgs e) => Persist();

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        Persist();
        if (ReferenceEquals(sender, WordCaptureSwitch))
            _services.ApplyWordCapture(_services.Store.LoadSettings().WordCaptureEnabled);
    }

    private void OnLlmApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var s = _services.Store.LoadSettings();
        s.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        _services.Store.SaveSettings(s);
    }

    private static void SetHotkeyButton(Button button, string? combo)
        => button.Content = string.IsNullOrWhiteSpace(combo) ? HotkeyEmptyLabel : combo;

    private static string? ReadHotkeyButton(Button button)
    {
        var text = button.Content?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text) || text is HotkeyEmptyLabel or HotkeyRecordingLabel)
            return null;
        return HotkeyParser.Canonicalize(text) ?? text;
    }

    private void OnHotkeyRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (_hotkeyRecordingButton == btn)
        {
            CancelHotkeyRecording(restore: true);
            return;
        }

        CancelHotkeyRecording(restore: true);
        _hotkeyRecordingButton = btn;
        _hotkeyRecordingPrevious = ReadHotkeyButton(btn);
        btn.Content = HotkeyRecordingLabel;
        btn.Focus(FocusState.Programmatic);
        SetLocalStatus("请按下快捷键（Esc 取消，Backspace 清空）");
        _services.SuspendHotkeys();
        _hotkeyPollSeen.Clear();
        for (var vk = 0x08; vk <= 0x87; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                _hotkeyPollSeen.Add(vk);
        }
        _hotkeyPollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _hotkeyPollTimer.Tick -= OnHotkeyPollTick;
        _hotkeyPollTimer.Tick += OnHotkeyPollTick;
        _hotkeyPollTimer.Start();
    }

    private void CancelHotkeyRecording(bool restore)
    {
        StopHotkeyPoll();
        if (_hotkeyRecordingButton is null) return;
        if (restore)
            SetHotkeyButton(_hotkeyRecordingButton, _hotkeyRecordingPrevious);
        _hotkeyRecordingButton = null;
        _hotkeyRecordingPrevious = null;
        _services.ReregisterHotkeys(_services.Store.LoadSettings());
    }

    private void StopHotkeyPoll()
    {
        if (_hotkeyPollTimer is null) return;
        _hotkeyPollTimer.Stop();
        _hotkeyPollTimer.Tick -= OnHotkeyPollTick;
        _hotkeyPollSeen.Clear();
    }

    private void OnHotkeyPollTick(object? sender, object e)
    {
        if (_hotkeyRecordingButton is null)
        {
            StopHotkeyPoll();
            return;
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        void Consider(int vk)
        {
            var down = Down(vk);
            if (!down)
            {
                _hotkeyPollSeen.Remove(vk);
                return;
            }
            if (!_hotkeyPollSeen.Add(vk)) return;
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C) return;

            if (vk == 0x1B)
            {
                CancelHotkeyRecording(restore: true);
                SetLocalStatus("");
                return;
            }

            if (vk is 0x08 or 0x2E)
            {
                var btn = _hotkeyRecordingButton!;
                StopHotkeyPoll();
                SetHotkeyButton(btn, null);
                _hotkeyRecordingButton = null;
                _hotkeyRecordingPrevious = null;
                Persist();
                SetLocalStatus("已清空热键");
                return;
            }

            var combo = HotkeyParser.FormatCapture(
                ctrl: Down(0x11),
                alt: Down(0x12),
                shift: Down(0x10),
                win: Down(0x5B) || Down(0x5C),
                virtualKey: (uint)vk);
            if (combo is null) return;

            var target = _hotkeyRecordingButton!;
            StopHotkeyPoll();
            SetHotkeyButton(target, combo);
            _hotkeyRecordingButton = null;
            _hotkeyRecordingPrevious = null;
            Persist();
            SetLocalStatus("热键已设为 " + combo);
        }

        for (var vk = 0x70; vk <= 0x87; vk++) Consider(vk);
        for (var vk = 0x30; vk <= 0x39; vk++) Consider(vk);
        for (var vk = 0x41; vk <= 0x5A; vk++) Consider(vk);
        for (var vk = 0x60; vk <= 0x6F; vk++) Consider(vk);
        foreach (var vk in (int[])[0x20, 0x09, 0x0D, 0x08, 0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x1B, 0xBB, 0xBD])
            Consider(vk);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void ApplyUpdateOutcome(UpdateCheckOutcome outcome)
    {
        UpdateStatusText.Text = outcome.Kind == UpdateCheckKind.UpdateAvailable
            ? $"发现新版本 v{outcome.RemoteVersion}（当前 v{outcome.CurrentVersion}）"
            : outcome.Message + $"（v{outcome.CurrentVersion}）";
        ApplyUpdateButton.Visibility = outcome.Kind == UpdateCheckKind.UpdateAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (outcome.Kind == UpdateCheckKind.UpdateAvailable && !string.IsNullOrWhiteSpace(outcome.Notes))
        {
            UpdateNotesText.Text = "更新说明：\n" + outcome.Notes;
            UpdateNotesText.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateNotesText.Text = "";
            UpdateNotesText.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        UpdateNotesText.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await _services.Updates.CheckAsync();
            ApplyUpdateOutcome(outcome);
            SetLocalStatus(outcome.Kind switch
            {
                UpdateCheckKind.UpdateAvailable => $"发现新版本 v{outcome.RemoteVersion}",
                UpdateCheckKind.Failed => outcome.Message.Split('\n')[0],
                _ => "已是最新版本",
            });
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            ApplyUpdateButton.IsEnabled = true;
        }
    }

    private async void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "安装更新",
            Content = "将下载并安装新版本，完成后 Glance 会自动重启。是否继续？",
            PrimaryButtonText = "更新并重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        CheckUpdateButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在下载更新…";
        var progress = new Progress<int>(p =>
            DispatcherQueue.TryEnqueue(() => UpdateStatusText.Text = $"正在下载更新… {p}%"));
        var result = await _services.Updates.DownloadAndApplyAsync(progress);
        UpdateStatusText.Text = result.Message;
        SetLocalStatus(result.Message);
        CheckUpdateButton.IsEnabled = true;
        ApplyUpdateButton.IsEnabled = true;
    }
}
