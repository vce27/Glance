using Glance.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Glance.App;

public sealed partial class WordCaptureWindow : Window
{

    private readonly YoudaoDictClient _dict = new();
    private int _lookupSeq;

    public WordCaptureWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        try { AppWindow.SetIcon(AppIconLoader.IconPath); } catch { /* optional */ }
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                HideQuiet();
        };
    }

    public void ShowLookup(string text, int screenX, int screenY)
    {
        var seq = ++_lookupSeq;
        HeadwordText.Text = text;
        PhoneticText.Visibility = Visibility.Collapsed;
        SenseList.ItemsSource = null;
        StatusText.Text = "查询中…";

        PositionNear(screenX, screenY);
        try { AppWindow.Show(); } catch { /* ignore */ }
        Activate();

        _ = LookupAsync(text, seq);
    }

    private async Task LookupAsync(string text, int seq)
    {
        try
        {
            var hit = await _dict.LookupAsync(text);
            if (seq != _lookupSeq) return;
            if (hit is null || (hit.Senses.Count == 0 && string.IsNullOrWhiteSpace(hit.Phonetic)))
            {
                StatusText.Text = "未找到释义";
                return;
            }

            if (!string.IsNullOrWhiteSpace(hit.Phonetic))
            {
                PhoneticText.Text = hit.Phonetic;
                PhoneticText.Visibility = Visibility.Visible;
            }

            SenseList.ItemsSource = hit.Senses
                .Take(4)
                .Select(s => new DictSenseRow
                {
                    Pos = string.IsNullOrWhiteSpace(s.Pos) ? "" : s.Pos,
                    Meaning = s.Meaning,
                })
                .ToList();
            StatusText.Text = "";
            DispatcherQueue.TryEnqueue(() => SnapHeight());
        }
        catch (Exception ex)
        {
            if (seq != _lookupSeq) return;
            StatusText.Text = "查询失败: " + ex.Message;
        }
    }

    private void PositionNear(int screenX, int screenY)
    {
        const int w = 340;
        const int h = 220;
        var x = screenX + 12;
        var y = screenY + 16;
        try
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(screenX, screenY), DisplayAreaFallback.Nearest)
                .WorkArea;
            if (x + w > area.X + area.Width) x = screenX - w - 12;
            if (y + h > area.Y + area.Height) y = screenY - h - 12;
            x = Math.Max(area.X, x);
            y = Math.Max(area.Y, y);
        }
        catch { /* ignore */ }

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void SnapHeight()
    {
        if (Content is not FrameworkElement root) return;
        root.Measure(new Windows.Foundation.Size(340, 800));
        var h = (int)Math.Ceiling(Math.Clamp(root.DesiredSize.Height + 8, 120, 360));
        var pos = AppWindow.Position;
        AppWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, 340, h));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideQuiet();

    public void HideQuiet()
    {
        try { AppWindow.Hide(); } catch { /* ignore */ }
    }
}
