using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Glance.App;

public sealed partial class WordbookWindow : Window
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new();

    public WordbookWindow()
    {
        InitializeComponent();
        SetTitleBar(AppTitleBar);
        ToolWindow.Configure(this, "生词本", 420, 520);
        Activated += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            _player.Pause();
            _player.Source = null;
            _synth.Dispose();
            _player.Dispose();
        };
    }

    public void Refresh()
    {
        var rows = AppServices.Current.Wordbook.Load()
            .Select(e => new WordbookRow
            {
                Id = e.Id,
                Word = e.Word,
                Phonetic = e.Phonetic ?? "",
                Translation = string.IsNullOrWhiteSpace(e.Translation) ? "（无译文）" : e.Translation,
            })
            .ToList();
        WordList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WordList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSpeakClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string word } || string.IsNullOrWhiteSpace(word))
            return;
        try
        {
            var stream = await _synth.SynthesizeTextToStreamAsync(word);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex)
        {
            AppServices.Current.ReportStatus("朗读失败: " + ex.Message);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        if (!AppServices.Current.Wordbook.Remove(id)) return;
        Refresh();
        AppServices.Current.ReportStatus("已从生词本删除");
    }
}
