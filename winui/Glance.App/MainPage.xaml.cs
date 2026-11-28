using System.Diagnostics;
using System.Text.RegularExpressions;
using Glance.Capture;
using Glance.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Glance.App;

public sealed class WordbookRow
{
    public string Id { get; init; } = "";
    public string Word { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public string Translation { get; init; } = "";
    public bool HasPhonetic => !string.IsNullOrWhiteSpace(Phonetic);
}

public sealed class DictSenseRow
{
    public string Pos { get; init; } = "";
    public string Meaning { get; init; } = "";
}

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
    private string _phonetic = "";
    private int _phoneticSeq;
    private bool _swapSpinning;
    private static readonly Regex HanRegex = new(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);

    public MainPage()
    {
        _services = AppServices.Current;
        InitializeComponent();
        ApplyTtsIcon(InputTtsButton, playing: false);
        ApplyTtsIcon(OutputTtsButton, playing: false);
        Loaded += OnLoaded;
        _mediaPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(ResetTtsButtons);
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
        WordCaptureButton.IsChecked = _settings.WordCaptureEnabled;
        PinButton.IsChecked = _settings.PinOnTop;
        UpdateThemeButtonGlyph();
        RefreshToolbarTooltips();
        ClearStatus();
        _services.ApplyPin(_settings.PinOnTop);
        _services.StatusChanged += msg => DispatcherQueue.TryEnqueue(() => SetStatus(msg));
        _services.CaptureCompleted += result => DispatcherQueue.TryEnqueue(() => ApplyCaptureResult(result));
        _loadingUi = false;

        if (_settings.AutoCheckUpdates)
            _ = AutoCheckUpdatesAsync();
    }

    public void CancelTransientUi() { /* hotkey recording lives in SettingsWindow */ }

    private void SetStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ClearStatus();
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SnapToContent();
    }

    private void ClearStatus()
    {
        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SnapToContent();
    }

    private string EngineLabel(TextTranslateEngine engine) => engine switch
    {
        TextTranslateEngine.Llm => "AI 大模型",
        TextTranslateEngine.Offline => "仅离线",
        _ => _services.Translator.Offline.Pack.IsInstalled() ? "必应（可离线备用）" : "必应",
    };

    private void RefreshToolbarTooltips()
    {
        var s = _services.Store.LoadSettings();
        ToolTipService.SetToolTip(SwapLangButton, "交换语言");
        ToolTipService.SetToolTip(WordCaptureButton, s.WordCaptureEnabled ? "划词取词：开" : "划词取词：关");
        ToolTipService.SetToolTip(PinButton, "置顶窗口");
        ToolTipService.SetToolTip(CaptureButton, Tip("截屏翻译", s.Hotkey));
        ToolTipService.SetToolTip(WordbookButton, "生词本");
        ToolTipService.SetToolTip(SettingsButton, "设置");
        UpdateThemeButtonGlyph();
    }

    private static string Tip(string name, string? hotkey)
        => string.IsNullOrWhiteSpace(hotkey) ? name : $"{name}  ({hotkey})";

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

    private static string LangLabel(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Content as string ?? "";

    private void PersistMainSettings()
    {
        if (_loadingUi) return;
        var s = _services.Store.LoadSettings();
        s.FromLang = LangValue(FromLangBox);
        s.ToLang = LangValue(ToLangBox);
        s.PinOnTop = PinButton.IsChecked == true;
        s.UiTheme = _settings.UiTheme;
        _settings = s;
        _services.Store.SaveSettings(s);
        RefreshToolbarTooltips();
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        PersistMainSettings();
        _ = TranslateNowAsync();
    }

    private void OnSwapLang(object sender, RoutedEventArgs e)
    {
        AnimateSwapIcon();
        var from = LangValue(FromLangBox);
        var to = LangValue(ToLangBox);
        if (from == "auto") from = "en";
        SelectLang(FromLangBox, to);
        SelectLang(ToLangBox, from);
        (InputBox.Text, OutputBox.Text) = (OutputBox.Text, InputBox.Text);
        (InputPinyinText.Text, OutputPinyinText.Text) = (OutputPinyinText.Text, InputPinyinText.Text);
        InputPinyinText.Visibility = string.IsNullOrWhiteSpace(InputPinyinText.Text) ? Visibility.Collapsed : Visibility.Visible;
        OutputPinyinText.Visibility = string.IsNullOrWhiteSpace(OutputPinyinText.Text) ? Visibility.Collapsed : Visibility.Visible;
        PersistMainSettings();
        _ = TranslateNowAsync();
    }

    private void AnimateSwapIcon()
    {
        if (_swapSpinning) return;
        _swapSpinning = true;
        var anim = new DoubleAnimation
        {
            From = SwapLangRotate.Angle,
            To = SwapLangRotate.Angle + 180,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => _swapSpinning = false;
        Storyboard.SetTarget(anim, SwapLangRotate);
        Storyboard.SetTargetProperty(anim, "Angle");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        PersistMainSettings();
        _services.ApplyPin(PinButton.IsChecked == true);
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        _settings.UiTheme = string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase)
            ? "light"
            : "dark";
        PersistMainSettings();
        UpdateThemeButtonGlyph();
        if (_services.MainWindow is MainWindow win)
            win.ApplyUiTheme(_settings.UiTheme);
        _services.ApplyToolWindowThemes(_settings.UiTheme);
    }

    private void UpdateThemeButtonGlyph()
    {
        var dark = string.Equals(_settings.UiTheme, "dark", StringComparison.OrdinalIgnoreCase);
        ThemeButton.Content = new FontIcon
        {
            FontFamily = FluentIcons,
            FontSize = 15,
            Glyph = dark ? GlyphSun : GlyphMoon,
        };
        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色主题" : "切换到深色主题");
    }

    private void OnWordCaptureToggle(object sender, RoutedEventArgs e)
    {
        var on = WordCaptureButton.IsChecked == true;
        var s = _services.Store.LoadSettings();
        s.WordCaptureEnabled = on;
        _services.Store.SaveSettings(s);
        _settings = s;
        _services.ApplyWordCapture(on);
        ToolTipService.SetToolTip(WordCaptureButton, on ? "划词取词：开" : "划词取词：关");
        SetStatus(on ? "划词取词已开启" : "划词取词已关闭");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _services.ShowSettingsWindow();
        // Settings may change hotkeys/engine — refresh next tick after store writes.
        DispatcherQueue.TryEnqueue(() =>
        {
            _settings = _services.Store.LoadSettings();
            WordCaptureButton.IsChecked = _settings.WordCaptureEnabled;
            RefreshToolbarTooltips();
            ClearStatus();
        });
    }

    private void OnWordbookClick(object sender, RoutedEventArgs e) => _services.ShowWordbookWindow();

    private void OnSaveWordClick(object sender, RoutedEventArgs e)
    {
        var word = InputBox.Text?.Trim() ?? "";
        var translation = OutputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(word))
        {
            SetStatus("先输入或截屏得到原文再收藏");
            return;
        }

        try
        {
            _services.Wordbook.Upsert(word, translation, _phonetic);
            SetStatus("已加入本地生词本");
            _services.RefreshWordbookWindow();
        }
        catch (Exception ex)
        {
            SetStatus("收藏失败: " + ex.Message);
        }
    }

    private async Task AutoCheckUpdatesAsync()
    {
        var outcome = await _services.Updates.CheckAsync();
        if (outcome.Kind == UpdateCheckKind.UpdateAvailable)
            _services.ReportStatus($"发现新版本 v{outcome.RemoteVersion}（设置里可更新）");
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
        => await _services.BeginCaptureAsync(CaptureMode.Translate);

    private void ApplyCaptureResult(CaptureSessionResult result)
    {
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
            _ = RefreshDictAsync(source.Trim(), _translateSeq);
        }
        else
        {
            ClearDict();
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            OutputBox.Text = target;
            SetStatus("截屏翻译完成 · 有道 OCR");
        }
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
            var langKey = forOutput ? LangValue(ToLangBox) : LangValue(FromLangBox);
            if (langKey == "auto") langKey = LooksChinese(text) ? "zh-CHS" : "en";
            var voiceLang = TtsLangMap.GetValueOrDefault(langKey, "zh-CN");
            TrySelectVoice(voiceLang);
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
            SetStatus("朗读失败: " + ex.Message);
            ResetTtsButtons();
        }
    }

    private void TrySelectVoice(string voiceLang)
    {
        try
        {
            var voice = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith(voiceLang, StringComparison.OrdinalIgnoreCase))
                ?? SpeechSynthesizer.AllVoices
                    .FirstOrDefault(v => v.Language.StartsWith(voiceLang.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (voice is not null)
                _synth.Voice = voice;
        }
        catch { /* keep default */ }
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
            FontSize = 13,
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
        _ = TranslateNowAsync(force: false);
    }

    private void OnClearInputClick(object sender, RoutedEventArgs e)
    {
        InputBox.Text = "";
        OutputBox.Text = "";
        ClearDict();
        SetSidePinyin(InputPinyinText, "");
        SetSidePinyin(OutputPinyinText, "");
        ClearStatus();
        InputBox.Focus(FocusState.Programmatic);
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SnapToContent();
    }

    private void OnRefreshTranslateClick(object sender, RoutedEventArgs e)
        => _ = TranslateNowAsync(force: true);

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var text = (tag == "output" ? OutputBox.Text : InputBox.Text)?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            SetStatus(tag == "output" ? "已复制译文" : "已复制原文");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败: " + ex.Message);
        }
    }

    private async Task TranslateNowAsync(bool force = false)
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
        {
            OutputBox.Text = "";
            ClearDict();
            SetSidePinyin(OutputPinyinText, "");
            ClearStatus();
            return;
        }

        var seq = ++_translateSeq;
        _ = RefreshDictAsync(text, seq);
        var engine = EngineLabel(_settings.TextTranslateEngine);
        try
        {
            PersistMainSettings();
            var settings = _services.Store.LoadSettings();
            _settings = settings;
            SetStatus(force ? $"重新翻译中 · {engine}" : $"翻译中 · {engine}");
            var sw = Stopwatch.StartNew();
            var result = await _services.Translator.TranslateAsync(
                text, LangValue(FromLangBox), LangValue(ToLangBox), settings);
            sw.Stop();
            if (seq != _translateSeq) return;
            OutputBox.Text = result.TranslatedText;

            var pinyin = result.Transliteration?.Trim() ?? "";
            if (string.IsNullOrEmpty(pinyin) && LooksChinese(result.TranslatedText))
            {
                try
                {
                    var dict = await _services.Dict.LookupAsync(result.TranslatedText);
                    if (seq != _translateSeq) return;
                    pinyin = dict?.Phonetic?.Trim() ?? "";
                }
                catch { /* optional */ }
            }

            if (seq != _translateSeq) return;
            ApplyOutputPinyin(result.TranslatedText, pinyin);
            ClearStatus();
        }
        catch (Exception ex)
        {
            if (seq != _translateSeq) return;
            SetStatus("翻译失败: " + ex.Message);
        }
    }

    private async Task RefreshDictAsync(string text, int translateSeq)
    {
        var seq = ++_phoneticSeq;
        try
        {
            var hit = await _services.Dict.LookupAsync(text);
            if (seq != _phoneticSeq || translateSeq != _translateSeq) return;
            ApplyDict(hit);
        }
        catch
        {
            if (seq != _phoneticSeq || translateSeq != _translateSeq) return;
            ClearDict();
        }
    }

    private void ClearDict()
    {
        _phonetic = "";
        DictPhoneticText.Text = "";
        DictPhoneticText.Visibility = Visibility.Collapsed;
        SetSidePinyin(InputPinyinText, "");
        DictSenseList.ItemsSource = null;
        DictSenseList.Visibility = Visibility.Collapsed;
        DictAltList.Children.Clear();
        DictAltBlock.Visibility = Visibility.Collapsed;
        DictPanel.Visibility = Visibility.Collapsed;
        if (AppServices.Current.MainWindow is MainWindow win)
            win.SnapToContent();
    }

    private void ApplyDict(DictLookup? hit)
    {
        if (hit is null)
        {
            ClearDict();
            return;
        }

        _phonetic = hit.Phonetic ?? "";
        var input = InputBox.Text?.Trim() ?? "";
        var inputIsHan = LooksChinese(input);

        // Chinese pinyin under the source pane; English IPA stays in the compact dict strip.
        if (inputIsHan && !string.IsNullOrWhiteSpace(_phonetic))
        {
            DictPhoneticText.Visibility = Visibility.Collapsed;
            SetSidePinyin(InputPinyinText, _phonetic);
        }
        else
        {
            SetSidePinyin(InputPinyinText, "");
            DictPhoneticText.Text = _phonetic;
            DictPhoneticText.Visibility = string.IsNullOrWhiteSpace(_phonetic)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        var senses = hit.Senses
            .Select(s => new DictSenseRow
            {
                Pos = string.IsNullOrWhiteSpace(s.Pos) ? "" : s.Pos,
                Meaning = s.Meaning,
            })
            .ToList();
        DictSenseList.ItemsSource = senses;
        DictSenseList.Visibility = senses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Prefer short POS glosses (Bing-style); only show alt chips when there are no senses.
        DictAltList.Children.Clear();
        var showAlts = senses.Count == 0 && hit.Alternatives.Count > 0;
        if (showAlts)
        {
            foreach (var alt in hit.Alternatives.Take(5))
            {
                var btn = new Button
                {
                    Content = alt,
                    Tag = alt,
                    Padding = new Thickness(8, 2, 8, 2),
                    MinHeight = 0,
                    FontSize = 12,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                };
                btn.Click += OnDictAltClick;
                DictAltList.Children.Add(btn);
            }
        }

        DictAltTitle.Text = string.IsNullOrWhiteSpace(input) ? "其他表达" : $"其他表达  {input}";
        DictAltBlock.Visibility = showAlts ? Visibility.Visible : Visibility.Collapsed;

        DictPanel.Visibility = senses.Count > 0 || showAlts
            || (!inputIsHan && !string.IsNullOrWhiteSpace(_phonetic))
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (AppServices.Current.MainWindow is MainWindow win)
            win.SnapToContent();
    }

    private void ApplyOutputPinyin(string translated, string transliteration)
    {
        if (!string.IsNullOrWhiteSpace(transliteration) && LooksChinese(translated))
        {
            SetSidePinyin(OutputPinyinText, transliteration.Trim());
            return;
        }

        if (!LooksChinese(translated))
            SetSidePinyin(OutputPinyinText, "");
    }

    private static void SetSidePinyin(TextBlock box, string value)
    {
        box.Text = value ?? "";
        box.Visibility = string.IsNullOrWhiteSpace(box.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool LooksChinese(string? text)
        => !string.IsNullOrWhiteSpace(text) && HanRegex.IsMatch(text);

    private void OnDictAltClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string alt } || string.IsNullOrWhiteSpace(alt))
            return;
        OutputBox.Text = alt;
        if (LooksChinese(alt) && !string.IsNullOrWhiteSpace(_phonetic) && !LooksChinese(InputBox.Text))
            SetSidePinyin(OutputPinyinText, _phonetic);
        else if (!LooksChinese(alt))
            SetSidePinyin(OutputPinyinText, "");
    }
}
