using Glance.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Glance.App;

public sealed partial class WordCaptureWindow : Window
{
    private readonly YoudaoDictClient _dict = new();
    private readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromSeconds(8) };
    private int _lookupSeq;
    private long _shownAtMs;

    public WordCaptureWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        // Opaque float — Mica/Acrylic lets terminal text bleed through as "残留".
        SystemBackdrop = null;
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
        _autoHide.Tick += (_, _) => HideQuiet();
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
        try { Activate(); } catch { /* OCR path often cannot steal focus */ }

        _shownAtMs = Environment.TickCount64;
        _autoHide.Stop();
        _autoHide.Start();

        _ = LookupAsync(text, seq);
    }

    /// <summary>
    /// OCR/终端场景下窗口常常拿不到焦点，Deactivated 不会来——靠全局鼠标按下关闭。
    /// </summary>
    public void DismissIfOutside(int screenX, int screenY)
    {
        if (!AppWindow.IsVisible) return;
        // Ignore the click/drag that just opened us.
        if (Environment.TickCount64 - _shownAtMs < 450) return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        if (screenX >= pos.X && screenX < pos.X + size.Width
            && screenY >= pos.Y && screenY < pos.Y + size.Height)
            return;

        HideQuiet();
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
        _autoHide.Stop();
        try { AppWindow.Hide(); } catch { /* ignore */ }
    }
}
