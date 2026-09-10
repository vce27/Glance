using Glance.Capture;
using Glance.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        UpdatePinVisual();
        AutostartSwitch.IsOn = _settings.Autostart;
        var dark = string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ThemeSwitch.IsOn = dark;
        ThemeButton.IsChecked = dark;
        ThemeButton.Content = dark ? "☀️" : "🌙";
        HotkeyBox.Text = _settings.Hotkey;
        CopyHotkeyBox.Text = _settings.CopyHotkey;
        PopupHotkeyBox.Text = _settings.PopupShortcut ?? "";
        CustomProxyBox.Text = _settings.CustomProxy;
        LlmBaseUrlBox.Text = _settings.LlmConfig.BaseUrl;
        LlmApiKeyBox.Password = _settings.LlmConfig.ApiKey;
        LlmModelBox.Text = _settings.LlmConfig.Model;
        ApplyProxyUi();
        HighlightEngine();
        UpdateEngineDependentUi();
        _services.ApplyPin(_settings.PinOnTop);
        _loadingUi = false;
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

    private void HighlightEngine()
    {
        foreach (var item in EngineList.Children)
        {
            if (item is not Button btn || btn.Tag is not TextTranslateEngine eng) continue;
            try
            {
                btn.Style = eng == _settings.TextTranslateEngine
                    ? (Style)Application.Current.Resources["GlancePrimaryButtonStyle"]
                    : (Style)Application.Current.Resources["GlanceGhostButtonStyle"];
            }
            catch
            {
                // Styles may be unavailable in some themes; ignore.
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
        _settings.UiTheme = ThemeSwitch.IsOn ? "dark" : "light";
        _settings.Hotkey = HotkeyBox.Text.Trim();
        _settings.CopyHotkey = CopyHotkeyBox.Text.Trim();
        var popup = PopupHotkeyBox.Text.Trim();
        _settings.PopupShortcut = string.IsNullOrEmpty(popup) ? null : popup;
        _settings.CustomProxy = CustomProxyBox.Text.Trim();
        _settings.LlmConfig.BaseUrl = LlmBaseUrlBox.Text.Trim();
        _settings.LlmConfig.ApiKey = LlmApiKeyBox.Password;
        _settings.LlmConfig.Model = LlmModelBox.Text.Trim();
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
        var pinned = PinButton.IsChecked == true;
        _services.ApplyPin(pinned);
        UpdatePinVisual();
        PersistSettings();
    }

    private void UpdatePinVisual()
    {
        // Legacy Glance: selected pin = accent tint, not solid blue fill.
        if (PinButton.IsChecked == true)
        {
            PinButton.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentSoftBrush"];
            PinButton.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemAccentBrush"];
        }
        else
        {
            PinButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            PinButton.ClearValue(Control.ForegroundProperty);
        }
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var dark = ThemeButton.IsChecked == true;
        ThemeSwitch.IsOn = dark;
        ApplyTheme(dark ? "dark" : "light");
    }

    private void OnThemeSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var dark = ThemeSwitch.IsOn;
        ThemeButton.IsChecked = dark;
        ApplyTheme(dark ? "dark" : "light");
    }

    private void ApplyTheme(string theme)
    {
        _settings.UiTheme = theme;
        ThemeButton.Content = theme == "dark" ? "☀️" : "🌙";
        if (AppServices.Current.MainWindow is MainWindow win)
            win.ApplyUiTheme(theme);
        PersistSettings();
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        var open = SettingsButton.IsChecked == true;
        SettingsScroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SetContentHeight(open);
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e) => PersistSettings();
    private void OnHotkeyCommit(object sender, RoutedEventArgs e) => PersistSettings();

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "截屏中…";
        await _services.BeginCaptureAsync(CaptureMode.Translate);
        StatusText.Text = "";
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
