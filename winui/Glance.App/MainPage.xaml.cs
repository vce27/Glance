using System.Runtime.InteropServices;
using Glance.Capture;
using Glance.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Glance.App;

public sealed partial class MainPage : Page
{
    private static readonly (string Value, string Label)[] Languages =
    [
        ("auto", "自动检测"),
        ("zh-CHS", "中文简体"),
        ("zh-CHT", "中文繁体"),
        ("en", "英语"),
        ("ja", "日语"),
        ("ko", "韩语"),
        ("fr", "法语"),
        ("de", "德语"),
        ("ru", "俄语"),
        ("es", "西班牙语"),
    ];

    private static readonly Dictionary<string, string> TtsLangMap = new()
    {
        ["zh-CHS"] = "zh-CN",
        ["zh-CN"] = "zh-CN",
        ["zh-CHT"] = "zh-TW",
        ["zh-TW"] = "zh-TW",
        ["en"] = "en-US",
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["fr"] = "fr-FR",
        ["de"] = "de-DE",
        ["ru"] = "ru-RU",
        ["es"] = "es-ES",
    };

    private static readonly FontFamily FluentIcons = new("Segoe Fluent Icons");
    private const string GlyphVolume = "\uE767";
    private const string GlyphStop = "\uE71A";
    private const string GlyphSun = "\uE706";
    private const string GlyphMoon = "\uE708";

    private readonly AppServices _services;
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _mediaPlayer = new();
    private TranslatorSettings _settings = new();
    private int _translateSeq;
    private DispatcherTimer? _debounce;
    private bool _loadingUi;
    private bool _ttsPlaying;
    private Button? _ttsActiveButton;
    private Button? _hotkeyRecordingButton;
    private string? _hotkeyRecordingPrevious;
    private DispatcherTimer? _hotkeyPollTimer;
    private readonly HashSet<int> _hotkeyPollSeen = [];
    private const string HotkeyEmptyLabel = "点击设置快捷键";
    private const string HotkeyRecordingLabel = "按下快捷键…";

    public MainPage()
    {
        _services = AppServices.Current;
        InitializeComponent();
        ApplyTtsIcon(InputTtsButton, playing: false);
        ApplyTtsIcon(OutputTtsButton, playing: false);

        Loaded += OnLoaded;
        _mediaPlayer.MediaEnded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(ResetTtsButtons);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadingUi = true;
        foreach (var (value, label) in Languages)
        {
            FromLangBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            ToLangBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        _settings = _services.Store.LoadSettings();
        SelectLang(FromLangBox, _settings.FromLang);
        SelectLang(ToLangBox, _settings.ToLang);
        PinButton.IsChecked = _settings.PinOnTop;
        AutostartSwitch.IsOn = _settings.Autostart;
        CompareOverlaySwitch.IsOn = _settings.ShowCompareOverlay;
        AutoUpdateSwitch.IsOn = _settings.AutoCheckUpdates;
        SetHotkeyButton(HotkeyButton, HotkeyParser.Canonicalize(_settings.Hotkey));
        SetHotkeyButton(CopyHotkeyButton, HotkeyParser.Canonicalize(_settings.CopyHotkey));
        SetHotkeyButton(PopupHotkeyButton, HotkeyParser.Canonicalize(_settings.PopupShortcut));
        CustomProxyBox.Text = _settings.CustomProxy;
        LlmBaseUrlBox.Text = _settings.LlmConfig.BaseUrl;
        LlmApiKeyBox.Password = _settings.LlmConfig.ApiKey;
        LlmModelBox.Text = _settings.LlmConfig.Model;
        ApplyProxyUi();
        UpdateThemeButtonGlyph();
        if (_settings.TextTranslateEngine == TextTranslateEngine.Llm)
            EngineLlm.IsChecked = true;
        else
            EngineBing.IsChecked = true;
        UpdateEngineDependentUi();
        _services.ApplyPin(_settings.PinOnTop);
        _services.StatusChanged += msg => DispatcherQueue.TryEnqueue(() => StatusText.Text = msg);
        _services.CaptureCompleted += result => DispatcherQueue.TryEnqueue(() => ApplyCaptureResult(result));
        _services.Capture.ComparePreferenceChanged += show => DispatcherQueue.TryEnqueue(() =>
        {
            if (_settings.ShowCompareOverlay == show) return;
            _loadingUi = true;
            _settings.ShowCompareOverlay = show;
            CompareOverlaySwitch.IsOn = show;
            _loadingUi = false;
        });
        UpdateStatusText.Text = $"当前版本 {_services.Updates.CurrentVersionDisplay}";
        ApplyUpdateButton.Visibility = Visibility.Collapsed;
        _loadingUi = false;

        if (_settings.AutoCheckUpdates)
            _ = AutoCheckUpdatesAsync();
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

    private void OnEngineChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.TextTranslateEngine = EngineLlm.IsChecked == true
            ? TextTranslateEngine.Llm
            : TextTranslateEngine.Bing;
        UpdateEngineDependentUi();
        PersistSettings();
        _ = TranslateNowAsync();
    }

    private void UpdateEngineDependentUi()
    {
        LlmPanel.Visibility = _settings.TextTranslateEngine == TextTranslateEngine.Llm
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Cancel hotkey recording before capture / modal flows.</summary>
    public bool IsSettingsOpen => SettingsScroller.Visibility == Visibility.Visible;

    public void CancelTransientUi() => CancelHotkeyRecording(restore: true);

    private static void SelectLang(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && Equals(item.Tag, value))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string LangValue(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

    private void PersistSettings()
    {
        if (_loadingUi) return;

        var prevHotkey = _settings.Hotkey;
        var prevCopy = _settings.CopyHotkey;
        var prevPopup = _settings.PopupShortcut;

        _settings.FromLang = LangValue(FromLangBox);
        _settings.ToLang = LangValue(ToLangBox);
        _settings.PinOnTop = PinButton.IsChecked == true;
        _settings.Autostart = AutostartSwitch.IsOn;
        _settings.ShowCompareOverlay = CompareOverlaySwitch.IsOn;
        _settings.CustomProxy = CustomProxyBox.Text.Trim();
        _settings.LlmConfig.BaseUrl = LlmBaseUrlBox.Text.Trim();
        _settings.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        _settings.LlmConfig.Model = LlmModelBox.Text.Trim();
        _settings.AutoCheckUpdates = AutoUpdateSwitch.IsOn;

        // While recording, never re-arm RegisterHotKey (it would swallow the chord).
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
            StatusText.Text = "热键冲突：同一快捷键不能用于多项，请改其中一个";
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
            // Keep previously working bindings on disk/UI when Windows rejects the new chord.
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

    private void OnProxyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        if (ProxyCustom.IsChecked == true) _settings.ProxyMode = ProxyMode.Custom;
        else if (ProxyNone.IsChecked == true) _settings.ProxyMode = ProxyMode.None;
        else _settings.ProxyMode = ProxyMode.System;
        CustomProxyBox.Visibility = _settings.ProxyMode == ProxyMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        PersistSettings();
    }

    private void OnSettingsFieldCommit(object sender, RoutedEventArgs e) => PersistSettings();

    private void OnLlmApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        _services.Store.SaveSettings(_settings);
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        PersistSettings();
        _ = TranslateNowAsync();
    }

    private void OnSwapLang(object sender, RoutedEventArgs e)
    {
        var from = LangValue(FromLangBox);
        var to = LangValue(ToLangBox);
        if (from == "auto") return;
        SelectLang(FromLangBox, to);
        SelectLang(ToLangBox, from);
        PersistSettings();
        (InputBox.Text, OutputBox.Text) = (OutputBox.Text, InputBox.Text);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _services.ApplyPin(PinButton.IsChecked == true);
        PersistSettings();
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var dark = !string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ApplyTheme(dark ? "dark" : "light");
    }

    private void ApplyTheme(string theme)
    {
        _settings.UiTheme = theme;
        UpdateThemeButtonGlyph();
        if (AppServices.Current.MainWindow is MainWindow win)
            win.ApplyUiTheme(theme);
        PersistSettings();
    }

    private void UpdateThemeButtonGlyph()
    {
        var dark = string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ThemeButton.Content = new FontIcon
        {
            FontFamily = FluentIcons,
            FontSize = 16,
            Glyph = dark ? GlyphSun : GlyphMoon,
        };
        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色主题" : "切换到深色主题");
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        var open = SettingsButton.IsChecked == true;
        if (!open)
            CancelHotkeyRecording(restore: true);
        SettingsScroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SetContentHeight(open);
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e) => PersistSettings();
    private void OnCompareOverlayToggled(object sender, RoutedEventArgs e) => PersistSettings();
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
        StatusText.Text = "请按下快捷键（Esc 取消，Backspace 清空）";

        // Global RegisterHotKey would swallow the chord (e.g. F4) before the UI
        // sees it — drop hooks while recording, then restore via PersistSettings.
        _services.SuspendHotkeys();
        _hotkeyPollSeen.Clear();
        // Ignore keys already down at click time; wait for a fresh press.
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
        // Re-arm whatever is already saved.
        _services.ReregisterHotkeys(_settings);
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

        // Edge-trigger: only act on keys newly pressed since recording started.
        void Consider(int vk)
        {
            var down = Down(vk);
            if (!down)
            {
                _hotkeyPollSeen.Remove(vk);
                return;
            }
            if (!_hotkeyPollSeen.Add(vk)) return;

            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C) return; // modifiers alone

            if (vk == 0x1B) // Esc
            {
                CancelHotkeyRecording(restore: true);
                StatusText.Text = "";
                return;
            }

            if (vk is 0x08 or 0x2E) // Backspace / Delete
            {
                var btn = _hotkeyRecordingButton!;
                StopHotkeyPoll();
                SetHotkeyButton(btn, null);
                _hotkeyRecordingButton = null;
                _hotkeyRecordingPrevious = null;
                PersistSettings();
                StatusText.Text = "已清空热键";
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
            PersistSettings();
            StatusText.Text = "热键已设为 " + combo;
        }

        // Function keys first — the usual screenshot chords.
        for (var vk = 0x70; vk <= 0x87; vk++) Consider(vk);
        // Letters / digits / numpad / common keys.
        for (var vk = 0x30; vk <= 0x39; vk++) Consider(vk);
        for (var vk = 0x41; vk <= 0x5A; vk++) Consider(vk);
        for (var vk = 0x60; vk <= 0x6F; vk++) Consider(vk);
        foreach (var vk in (int[])[0x20, 0x09, 0x0D, 0x08, 0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x1B, 0xBB, 0xBD])
            Consider(vk);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e) => PersistSettings();

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

    private async Task AutoCheckUpdatesAsync()
    {
        var outcome = await _services.Updates.CheckAsync();
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyUpdateOutcome(outcome);
            if (outcome.Kind == UpdateCheckKind.UpdateAvailable)
                _services.ReportStatus($"发现新版本 v{outcome.RemoteVersion}");
        });
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
            StatusText.Text = outcome.Kind switch
            {
                UpdateCheckKind.UpdateAvailable => $"发现新版本 v{outcome.RemoteVersion}，点击「立即更新并重启」安装",
                UpdateCheckKind.Failed => outcome.Message.Split('\n')[0],
                _ => "已是最新版本",
            };
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
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;
        await DownloadAndRestartAsync();
    }

    private async Task DownloadAndRestartAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在下载更新…";
        StatusText.Text = "正在下载更新…";
        var progress = new Progress<int>(p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStatusText.Text = $"正在下载更新… {p}%";
                StatusText.Text = $"正在下载更新… {p}%";
            });
        });

        var result = await _services.Updates.DownloadAndApplyAsync(progress);
        // ApplyUpdatesAndRestart exits the process on success; only failures reach here.
        UpdateStatusText.Text = result.Message;
        StatusText.Text = result.Message;
        CheckUpdateButton.IsEnabled = true;
        ApplyUpdateButton.IsEnabled = true;
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        await _services.BeginCaptureAsync(CaptureMode.Translate);
    }

    private void ApplyCaptureResult(CaptureSessionResult result)
    {
        // Overlay send-to-main / OCR-copy: fill both panes so TTS has 原文 + 译文.
        var source = result.CopiedText?.Trim() ?? "";
        var target = "";
        if (result.Translation?.Pairs is { Count: > 0 } pairs)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                source = string.Join("\n", pairs
                    .Select(p => p.Source)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            target = string.Join("\n", pairs
                .Select(p => p.Target)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            InputBox.Text = source;
            InputBox.Focus(FocusState.Programmatic);
        }
        if (!string.IsNullOrWhiteSpace(target))
            OutputBox.Text = target;
    }

    private async void OnTtsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var forOutput = Equals(button.Tag, "output");
        var text = (forOutput ? OutputBox.Text : InputBox.Text)?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        if (_ttsPlaying)
        {
            var same = ReferenceEquals(_ttsActiveButton, button);
            StopTts();
            if (same) return;
        }

        try
        {
            var lang = LangValue(forOutput ? ToLangBox : FromLangBox);
            if (lang == "auto") lang = "zh-CHS";
            var bcp47 = TtsLangMap.GetValueOrDefault(lang, lang);
            var voice = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith(bcp47.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? SpeechSynthesizer.DefaultVoice;
            _synth.Voice = voice;
            var stream = await _synth.SynthesizeTextToStreamAsync(text);
            _mediaPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _mediaPlayer.Play();
            _ttsPlaying = true;
            _ttsActiveButton = button;
            ApplyTtsIcon(InputTtsButton, playing: false);
            ApplyTtsIcon(OutputTtsButton, playing: false);
            ApplyTtsIcon(button, playing: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "朗读失败: " + ex.Message;
            ResetTtsButtons();
        }
    }

    private void StopTts()
    {
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        ResetTtsButtons();
    }

    private void ResetTtsButtons()
    {
        _ttsPlaying = false;
        _ttsActiveButton = null;
        ApplyTtsIcon(InputTtsButton, playing: false);
        ApplyTtsIcon(OutputTtsButton, playing: false);
    }

    private static void ApplyTtsIcon(Button button, bool playing)
    {
        button.Content = new FontIcon
        {
            FontFamily = FluentIcons,
            FontSize = 14,
            Glyph = playing ? GlyphStop : GlyphVolume,
        };
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick -= DebounceTick;
        _debounce.Tick += DebounceTick;
        _debounce.Stop();
        _debounce.Start();
    }

    private void DebounceTick(object? sender, object e)
    {
        _debounce?.Stop();
        _ = TranslateNowAsync();
    }

    private async Task TranslateNowAsync()
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
        {
            OutputBox.Text = "";
            return;
        }

        var seq = ++_translateSeq;
        try
        {
            PersistSettings();
            var result = await _services.Translator.TranslateAsync(
                text, LangValue(FromLangBox), LangValue(ToLangBox), _settings);
            if (seq != _translateSeq) return;
            OutputBox.Text = result.TranslatedText;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            if (seq != _translateSeq) return;
            StatusText.Text = "翻译失败: " + ex.Message;
        }
    }
}
