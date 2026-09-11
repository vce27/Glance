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

    private static readonly (TextTranslateEngine Engine, string Label)[] Engines =
    [
        (TextTranslateEngine.Bing, "必应"),
        (TextTranslateEngine.Google, "Google"),
        (TextTranslateEngine.Microsoft, "微软"),
        (TextTranslateEngine.Transmart, "腾讯"),
        (TextTranslateEngine.Yandex, "Yandex"),
        (TextTranslateEngine.Iciba, "词霸"),
        (TextTranslateEngine.Llm, "AI 大模型"),
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

    private readonly AppServices _services;
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _mediaPlayer = new();
    private TranslatorSettings _settings = new();
    private int _translateSeq;
    private DispatcherTimer? _debounce;
    private bool _loadingUi;
    private bool _ttsPlaying;

    public MainPage()
    {
        _services = AppServices.Current;
        InitializeComponent();
        Loaded += OnLoaded;
        _mediaPlayer.MediaEnded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _ttsPlaying = false;
                TtsButton.Content = "🔊";
            });
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

        foreach (var (engine, label) in Engines)
        {
            var btn = new Button
            {
                Content = label,
                Tag = engine,
                Margin = new Thickness(0, 0, 8, 8),
                MinWidth = 72,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            btn.Click += OnEngineButtonClick;
            EngineList.Children.Add(btn);
        }

        _settings = _services.Store.LoadSettings();
        SelectLang(FromLangBox, _settings.FromLang);
        SelectLang(ToLangBox, _settings.ToLang);
        PinButton.IsChecked = _settings.PinOnTop;
        AutostartSwitch.IsOn = _settings.Autostart;
        CompareOverlaySwitch.IsOn = _settings.ShowCompareOverlay;
        var dark = string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ThemeSwitch.IsOn = dark;
        AutoUpdateSwitch.IsOn = _settings.AutoCheckUpdates;
        HotkeyBox.Text = _settings.Hotkey;
        CopyHotkeyBox.Text = _settings.CopyHotkey;
        PopupHotkeyBox.Text = _settings.PopupShortcut ?? "";
        CustomProxyBox.Text = _settings.CustomProxy;
        LlmBaseUrlBox.Text = _settings.LlmConfig.BaseUrl;
        LlmApiKeyBox.Password = _settings.LlmConfig.ApiKey;
        LlmModelBox.Text = _settings.LlmConfig.Model;
        ApplyProxyUi();
        UpdateThemeButtonGlyph();
        HighlightEngine();
        UpdateEngineDependentUi();
        _services.ApplyPin(_settings.PinOnTop);
        _services.StatusChanged += msg => DispatcherQueue.TryEnqueue(() => StatusText.Text = msg);
        _services.CaptureCompleted += result => DispatcherQueue.TryEnqueue(() => ApplyCaptureResult(result));
        UpdateStatusText.Text = $"当前版本 {_services.Updates.CurrentVersionDisplay}";
        ApplyUpdateButton.Visibility = Visibility.Collapsed;
        _loadingUi = false;

        if (_settings.AutoCheckUpdates)
            _ = AutoCheckUpdatesAsync();
    }

    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush WhiteBrush = new(Microsoft.UI.Colors.White);
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private static readonly FontFamily FluentIcons = new("Segoe Fluent Icons");
    private FontIcon? _themeSunIcon;
    private FontIcon? _themeMoonIcon;

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

    private void HighlightEngine()
    {
        foreach (var item in EngineList.Children)
        {
            if (item is not Button btn || btn.Tag is not TextTranslateEngine eng) continue;
            var selected = eng == _settings.TextTranslateEngine;
            btn.BorderBrush = AccentBrush;
            btn.BorderThickness = new Thickness(1);
            if (selected)
            {
                btn.Background = AccentBrush;
                btn.Foreground = WhiteBrush;
            }
            else
            {
                btn.Background = TransparentBrush;
                btn.Foreground = AccentBrush;
            }
        }
    }

    private void OnEngineButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TextTranslateEngine engine) return;
        _settings.TextTranslateEngine = engine;
        HighlightEngine();
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
        _settings.FromLang = LangValue(FromLangBox);
        _settings.ToLang = LangValue(ToLangBox);
        _settings.PinOnTop = PinButton.IsChecked == true;
        _settings.Autostart = AutostartSwitch.IsOn;
        _settings.ShowCompareOverlay = CompareOverlaySwitch.IsOn;
        _settings.UiTheme = ThemeSwitch.IsOn ? "dark" : "light";
        _settings.Hotkey = HotkeyBox.Text.Trim();
        _settings.CopyHotkey = CopyHotkeyBox.Text.Trim();
        var popup = PopupHotkeyBox.Text.Trim();
        _settings.PopupShortcut = string.IsNullOrEmpty(popup) ? null : popup;
        _settings.CustomProxy = CustomProxyBox.Text.Trim();
        _settings.LlmConfig.BaseUrl = LlmBaseUrlBox.Text.Trim();
        _settings.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        _settings.LlmConfig.Model = LlmModelBox.Text.Trim();
        _settings.AutoCheckUpdates = AutoUpdateSwitch.IsOn;
        _services.Store.SaveSettings(_settings);
        _services.ApplyAutostart(_settings.Autostart);
        _services.ReregisterHotkeys(_settings);
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
        ThemeSwitch.IsOn = dark;
        ApplyTheme(dark ? "dark" : "light");
    }

    private void OnThemeSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyTheme(ThemeSwitch.IsOn ? "dark" : "light");
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
        _themeSunIcon ??= new FontIcon { Glyph = "\uE706", FontSize = 16, FontFamily = FluentIcons }; // WeatherSunny
        _themeMoonIcon ??= new FontIcon { Glyph = "\uE708", FontSize = 16, FontFamily = FluentIcons }; // WeatherMoon
        ThemeButton.Content = dark ? _themeSunIcon : _themeMoonIcon;
        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色主题" : "切换到深色主题");
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        var open = SettingsButton.IsChecked == true;
        SettingsScroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SetContentHeight(open);
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e) => PersistSettings();
    private void OnCompareOverlayToggled(object sender, RoutedEventArgs e) => PersistSettings();
    private void OnHotkeyCommit(object sender, RoutedEventArgs e) => PersistSettings();

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
            StatusText.Text = outcome.Kind is UpdateCheckKind.UpdateAvailable or UpdateCheckKind.Failed
                ? outcome.Message.Split('\n')[0]
                : "";

            // One-click: if update found, download + restart immediately (DeskBox-style).
            if (outcome.Kind == UpdateCheckKind.UpdateAvailable)
                await DownloadAndRestartAsync();
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            ApplyUpdateButton.IsEnabled = true;
        }
    }

    private async void OnApplyUpdateClick(object sender, RoutedEventArgs e)
        => await DownloadAndRestartAsync();

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
        if (!string.IsNullOrWhiteSpace(result.CopiedText))
        {
            InputBox.Text = result.CopiedText;
            return;
        }

        var pairs = result.Translation?.Pairs;
        if (pairs is null || pairs.Count == 0) return;
        InputBox.Text = string.Join("\n", pairs.Select(p => p.Source).Where(s => !string.IsNullOrWhiteSpace(s)));
        OutputBox.Text = string.Join("\n", pairs.Select(p => p.Target).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private async void OnTtsClick(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        if (_ttsPlaying)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
            _ttsPlaying = false;
            TtsButton.Content = "🔊";
            return;
        }

        try
        {
            var lang = LangValue(FromLangBox);
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
            TtsButton.Content = "⏹";
        }
        catch (Exception ex)
        {
            StatusText.Text = "朗读失败: " + ex.Message;
            _ttsPlaying = false;
            TtsButton.Content = "🔊";
        }
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
