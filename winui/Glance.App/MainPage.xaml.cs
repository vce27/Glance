using Glance.Capture;
using Glance.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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

    private readonly AppServices _services;
    private TranslatorSettings _settings = new();
    private int _translateSeq;
    private DispatcherTimer? _debounce;
    private bool _loadingUi;

    public MainPage()
    {
        _services = AppServices.Current;
        InitializeComponent();
        Loaded += OnLoaded;
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
        HotkeyBox.Text = _settings.Hotkey;
        CopyHotkeyBox.Text = _settings.CopyHotkey;
        PopupHotkeyBox.Text = _settings.PopupShortcut ?? "";
        _services.ApplyPin(_settings.PinOnTop);
        _loadingUi = false;
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
        _settings.Hotkey = HotkeyBox.Text.Trim();
        _settings.CopyHotkey = CopyHotkeyBox.Text.Trim();
        var popup = PopupHotkeyBox.Text.Trim();
        _settings.PopupShortcut = string.IsNullOrEmpty(popup) ? null : popup;
        _services.Store.SaveSettings(_settings);
        _services.ApplyAutostart(_settings.Autostart);
        _services.ReregisterHotkeys(_settings);
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
        PersistSettings();
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e) => PersistSettings();

    private void OnHotkeyCommit(object sender, RoutedEventArgs e) => PersistSettings();

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "截屏中…";
        await _services.BeginCaptureAsync(CaptureMode.Translate);
        StatusText.Text = "";
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
            var result = await _services.Bing.TranslateAsync(text, LangValue(FromLangBox), LangValue(ToLangBox));
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
