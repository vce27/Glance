using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Glance.Core;

namespace Glance.Capture;

public enum CaptureOverlayResultKind
{
    Cancelled,
    Selection,
}

public sealed class CaptureOverlayResult
{
    public CaptureOverlayResultKind Kind { get; init; }
    public Rectangle Selection { get; init; }
}

/// <summary>
/// Single-monitor borderless selection overlay. Pre-paints darkened screenshot
/// before Show to avoid white flash / mixed-DPI black HWND issues.
/// </summary>
public sealed class CaptureOverlayForm : Form
{
    private const int HandleRadius = 5;
    private const int CardPadX = 12;
    private const int CardPadY = 8;
    private const int CardGap = 6;
    private const int MinSelection = 8;
    private const int ToggleW = 36;
    private const int ToggleH = 20;
    private const int ToggleGap = 4;
    private const int TogglePadX = 8;
    private const string CompareLabel = "对比";
    private readonly Font _toggleLabelFont = new("Segoe UI", 9f, FontStyle.Regular);

    private readonly Bitmap _screen;
    private readonly Bitmap _dimmed;
    private readonly Font _uiFont = new("Segoe UI", 11f, FontStyle.Regular);
    private readonly Font _uiFontItalic = new("Segoe UI", 11f, FontStyle.Italic);
    private readonly SolidBrush _whiteBrush = new(Color.White);
    private readonly SolidBrush _targetBrush = new(Color.FromArgb(90, 100, 120));
    private readonly SolidBrush _statusBg = new(Color.FromArgb(200, 32, 32, 32));
    private readonly SolidBrush _handleFill = new(Color.FromArgb(0, 120, 212));
    private readonly SolidBrush _toggleOnBg = new(Color.FromArgb(0x52, 0xC4, 0x1A));
    private readonly SolidBrush _toggleOffBg = new(Color.FromArgb(0xBF, 0xBF, 0xBF));
    private readonly SolidBrush _toggleThumb = new(Color.White);
    private readonly Pen _accentPen = new(Color.FromArgb(0, 120, 212), 2);
    private readonly Pen _handleOutline = new(Color.White, 1.5f);
    private readonly Pen _cardBorder = new(Color.FromArgb(220, 220, 220));
    private readonly StringFormat _centerFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };
    private readonly StringFormat _cardFormat = new()
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisWord,
    };

    private Point? _start;
    private Rectangle _selection;
    private bool _selecting;
    private Bitmap? _resultImage;
    private bool _showingResult;
    private string _targetText = "";
    private string? _statusText;
    private Rectangle _translationCard;
    private int _translationCardHeight = 28;
    private Rectangle _compareToggle;
    private bool _showCompare = true;

    /// <summary>When true: original in selection + translation card. When false: translation fills selection.</summary>
    public bool ShowCompareOverlay
    {
        get => _showCompare;
        set
        {
            if (_showCompare == value) return;
            _showCompare = value;
            if (_showingResult) Invalidate();
        }
    }

    public event Action<bool>? CompareOverlayChanged;

    private enum DragMode { None, MoveSelection, ResizeSelection, Reselect }
    private DragMode _dragMode;
    private Point _dragOrigin;
    private Rectangle _dragStartSelection;
    private int _activeHandle = -1;
    private bool _selectionChanged;

    private CaptureOverlayResult? _result;
    public CaptureOverlayResult? Result
    {
        get => _result;
        private set
        {
            _result = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when Result / show-state changes so the host can stop polling.</summary>
    public event EventHandler? Changed;

    public CaptureOverlayForm(MonitorSnapshot snapshot)
    {
        // Take ownership of the capture bitmap (no extra full-screen clone).
        _screen = snapshot.Bitmap;
        _dimmed = CreateDimmed(_screen);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(snapshot.Monitor.X, snapshot.Monitor.Y, snapshot.Monitor.Width, snapshot.Monitor.Height);
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        BackColor = Color.Black;
        // Dimmed background paints only the invalid region; OnPaint draws chrome on top.
        BackgroundImage = _dimmed;
        BackgroundImageLayout = ImageLayout.None;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void ShowLoading(string text = "翻译中…")
    {
        _statusText = text;
        _showingResult = false;
        _resultImage?.Dispose();
        _resultImage = null;
        _targetText = "";
        Result = null;
        Cursor = Cursors.Cross;
        Invalidate();
        Update();
    }

    public void AwaitNextSelection()
    {
        Result = null;
        _showingResult = false;
        Cursor = Cursors.Cross;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowTranslatedResult(byte[]? jpegBytes, IReadOnlyList<TranslationPair>? pairs = null)
    {
        var list = pairs?.Where(p => !string.IsNullOrWhiteSpace(p.Source) || !string.IsNullOrWhiteSpace(p.Target)).ToList()
                   ?? [];
        _targetText = string.Join("\n", list.Select(p => p.Target).Where(s => !string.IsNullOrWhiteSpace(s)));

        if (jpegBytes is { Length: > 0 })
        {
            using var ms = new MemoryStream(jpegBytes);
            _resultImage?.Dispose();
            _resultImage = new Bitmap(ms);
        }
        else
        {
            _resultImage?.Dispose();
            _resultImage = null;
        }

        _showingResult = true;
        _statusText = null;
        _translationCardHeight = MeasureTranslationHeight();
        _translationCard = LayoutTranslationCard();
        Cursor = Cursors.Default;
        Result = null;
        Invalidate();
        Update();
    }

    public bool IsShowingResult => _showingResult && !IsDisposed;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // BackgroundImage already paints _dimmed into the dirty region — don't blit full screen again.

        if (_showingResult)
        {
            DrawResultViews(g);
            return;
        }

        if (!_selection.IsEmpty)
        {
            g.SetClip(_selection, CombineMode.Replace);
            g.DrawImage(_screen, 0, 0, Width, Height);
            g.ResetClip();
            g.DrawRectangle(_accentPen, _selection);
        }

        if (!string.IsNullOrEmpty(_statusText))
        {
            var rect = _selection.IsEmpty
                ? new Rectangle(Width / 2 - 80, Height / 2 - 20, 160, 40)
                : new Rectangle(_selection.X, Math.Max(0, _selection.Y - 36), Math.Max(120, _selection.Width), 32);
            g.FillRectangle(_statusBg, rect);
            g.DrawString(_statusText, _uiFont, _whiteBrush, rect, _centerFormat);
        }
    }

    private void DrawResultViews(Graphics g)
    {
        if (_selection.IsEmpty) return;

        var adjusting = _dragMode is DragMode.MoveSelection or DragMode.ResizeSelection;

        g.SetClip(_selection, CombineMode.Replace);
        if (_showCompare || adjusting)
        {
            // Compare on: first layer keeps original screenshot pixels.
            g.DrawImage(_screen, 0, 0, Width, Height);
        }
        else
        {
            // Compare off: selection shows translated content directly.
            DrawTranslationInSelection(g);
        }
        g.ResetClip();

        g.DrawRectangle(_accentPen, _selection);
        foreach (var h in EnumerateHandles(_selection))
        {
            var r = HandleRect(h);
            g.FillEllipse(_handleFill, r);
            g.DrawEllipse(_handleOutline, r);
        }

        _compareToggle = LayoutCompareToggle();
        DrawCompareToggle(g);

        if (adjusting || !_showCompare) return;

        _translationCard = LayoutTranslationCard();
        if (_translationCard.IsEmpty) return;

        g.FillRectangle(_whiteBrush, _translationCard);
        g.DrawRectangle(_cardBorder, _translationCard);
        DrawTranslationContent(g, _translationCard);
    }

    private void DrawTranslationInSelection(Graphics g)
    {
        // Cover original completely — selection itself is the translated view.
        g.FillRectangle(_whiteBrush, _selection);

        if (!string.IsNullOrWhiteSpace(_targetText))
        {
            var textRect = Rectangle.Inflate(_selection, -CardPadX, -CardPadY);
            using var brush = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.DrawString(_targetText, _uiFont, brush, textRect, _cardFormat);
            return;
        }

        if (_resultImage is not null)
        {
            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_resultImage, _selection, new Rectangle(0, 0, _resultImage.Width, _resultImage.Height), GraphicsUnit.Pixel);
            g.InterpolationMode = old;
        }
    }

    private void DrawTranslationContent(Graphics g, Rectangle bounds)
    {
        if (!string.IsNullOrWhiteSpace(_targetText))
        {
            var textRect = Rectangle.Inflate(bounds, -CardPadX, -CardPadY);
            g.DrawString(_targetText, _uiFontItalic, _targetBrush, textRect, _cardFormat);
            return;
        }

        if (_resultImage is not null)
        {
            g.SetClip(bounds, CombineMode.Replace);
            g.DrawImage(_resultImage, bounds, new Rectangle(0, 0, _resultImage.Width, _resultImage.Height), GraphicsUnit.Pixel);
            g.ResetClip();
        }
    }

    private void DrawCompareToggle(Graphics g)
    {
        if (_compareToggle.IsEmpty) return;

        var labelW = MeasureCompareLabelWidth();
        using (var labelBg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        using (var shell = RoundedRect(_compareToggle, ToggleH / 2))
            g.FillPath(labelBg, shell);

        var labelRect = new Rectangle(
            _compareToggle.X + TogglePadX,
            _compareToggle.Y,
            labelW,
            ToggleH);
        g.DrawString(CompareLabel, _toggleLabelFont, _whiteBrush, labelRect, _centerFormat);

        var track = new Rectangle(
            _compareToggle.X + TogglePadX + labelW + ToggleGap,
            _compareToggle.Y + 2,
            ToggleW,
            ToggleH - 4);

        var trackBg = _showCompare ? _toggleOnBg : _toggleOffBg;
        using (var trackPath = RoundedRect(track, track.Height / 2))
            g.FillPath(trackBg, trackPath);

        const int pad = 2;
        var thumbSize = track.Height - pad * 2;
        var thumbX = _showCompare
            ? track.Right - pad - thumbSize
            : track.X + pad;
        var thumb = new Rectangle(thumbX, track.Y + pad, thumbSize, thumbSize);
        g.FillEllipse(_toggleThumb, thumb);
    }

    private int MeasureCompareLabelWidth()
    {
        var size = TextRenderer.MeasureText(
            CompareLabel,
            _toggleLabelFont,
            new Size(int.MaxValue, ToggleH),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return Math.Max(28, size.Width);
    }

    private Rectangle LayoutCompareToggle()
    {
        if (_selection.IsEmpty) return Rectangle.Empty;
        var labelW = MeasureCompareLabelWidth();
        var totalW = TogglePadX + labelW + ToggleGap + ToggleW + TogglePadX;
        var x = Math.Clamp(_selection.Right - totalW, 4, Math.Max(4, Width - totalW - 4));
        var y = _selection.Y - ToggleH - 6;
        if (y < 4) y = Math.Min(_selection.Bottom + 6, Height - ToggleH - 4);
        return new Rectangle(x, y, totalW, ToggleH);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private int MeasureTranslationHeight()
    {
        if (!string.IsNullOrWhiteSpace(_targetText))
        {
            var width = Math.Max(40, (_selection.IsEmpty ? 200 : _selection.Width) - CardPadX * 2);
            var size = TextRenderer.MeasureText(
                _targetText,
                _uiFontItalic,
                new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return Math.Max(28, size.Height + CardPadY * 2);
        }
        if (_resultImage is not null && !_selection.IsEmpty)
            return Math.Max(28, _selection.Height);
        return 28;
    }

    private Rectangle LayoutTranslationCard()
    {
        if (_selection.IsEmpty) return Rectangle.Empty;
        var h = _translationCardHeight;
        var y = Math.Min(_selection.Bottom + CardGap, Height - h - 4);
        if (y < _selection.Bottom) y = Math.Max(4, _selection.Y - h - CardGap);
        return new Rectangle(_selection.X, y, Math.Max(40, _selection.Width), h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Finish(CaptureOverlayResultKind.Cancelled);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        _dragOrigin = e.Location;
        _selectionChanged = false;

        if (_showingResult)
        {
            _compareToggle = LayoutCompareToggle();
            if (!_compareToggle.IsEmpty && _compareToggle.Contains(e.Location))
            {
                _showCompare = !_showCompare;
                CompareOverlayChanged?.Invoke(_showCompare);
                Invalidate();
                return;
            }

            var handle = HitTestHandle(e.Location, _selection);
            if (handle >= 0)
            {
                _dragMode = DragMode.ResizeSelection;
                _activeHandle = handle;
                _dragStartSelection = _selection;
                return;
            }

            if (_selection.Contains(e.Location))
            {
                _dragMode = DragMode.MoveSelection;
                _dragStartSelection = _selection;
                Cursor = Cursors.SizeAll;
                return;
            }

            _dragMode = DragMode.Reselect;
            _showingResult = false;
            _selecting = true;
            _start = e.Location;
            _selection = new Rectangle(e.Location, Size.Empty);
            Cursor = Cursors.Cross;
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dragMode = DragMode.None;
        _selecting = true;
        _start = e.Location;
        _selection = new Rectangle(e.Location, Size.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragMode == DragMode.MoveSelection)
        {
            var dx = e.X - _dragOrigin.X;
            var dy = e.Y - _dragOrigin.Y;
            if (dx != 0 || dy != 0) _selectionChanged = true;
            var next = _dragStartSelection;
            next.Offset(dx, dy);
            next = ClampToForm(next);
            if (next != _selection)
            {
                var before = _selection;
                _selection = next;
                InvalidateSelectionChange(before, _selection);
            }
            return;
        }

        if (_dragMode == DragMode.ResizeSelection)
        {
            if (e.Location != _dragOrigin) _selectionChanged = true;
            var next = ResizeFromHandle(_dragStartSelection, _activeHandle, e.Location);
            if (next != _selection)
            {
                var before = _selection;
                _selection = next;
                InvalidateSelectionChange(before, _selection);
            }
            return;
        }

        if (_showingResult && _dragMode == DragMode.None)
        {
            _compareToggle = LayoutCompareToggle();
            if (!_compareToggle.IsEmpty && _compareToggle.Contains(e.Location))
            {
                Cursor = Cursors.Hand;
                return;
            }

            var handle = HitTestHandle(e.Location, _selection);
            Cursor = handle >= 0
                ? CursorForHandle(handle)
                : (_selection.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default);
            return;
        }

        if (!_selecting || _start is null) return;
        var sel = NormalizeRect(_start.Value, e.Location);
        if (sel != _selection)
        {
            var before = _selection;
            _selection = sel;
            InvalidateSelectionChange(before, _selection);
        }
    }

    private void InvalidateSelectionChange(Rectangle before, Rectangle after)
    {
        const int pad = HandleRadius + 4;
        var dirty = Rectangle.Union(InflateRect(before, pad), InflateRect(after, pad));
        if (_showingResult || _translationCardHeight > 0)
        {
            var band = Math.Max(_translationCardHeight + CardGap + 4, 40);
            dirty = Rectangle.Union(dirty, new Rectangle(before.X, before.Bottom, Math.Max(1, before.Width), band));
            dirty = Rectangle.Union(dirty, new Rectangle(after.X, after.Bottom, Math.Max(1, after.Width), band));
            dirty = Rectangle.Union(dirty, new Rectangle(before.X, before.Y - band, Math.Max(1, before.Width), band));
            dirty = Rectangle.Union(dirty, new Rectangle(after.X, after.Y - band, Math.Max(1, after.Width), band));
            dirty = Rectangle.Union(dirty, InflateRect(LayoutCompareToggle(), 4));
        }
        dirty.Intersect(ClientRectangle);
        if (!dirty.IsEmpty) Invalidate(dirty);
    }

    private static Rectangle InflateRect(Rectangle r, int pad)
    {
        if (r.IsEmpty) return r;
        r.Inflate(pad, pad);
        return r;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_dragMode is DragMode.MoveSelection or DragMode.ResizeSelection)
        {
            _dragMode = DragMode.None;
            _activeHandle = -1;
            Cursor = Cursors.Default;

            if (_selectionChanged && _selection.Width >= MinSelection && _selection.Height >= MinSelection)
            {
                _showingResult = false;
                Result = new CaptureOverlayResult
                {
                    Kind = CaptureOverlayResultKind.Selection,
                    Selection = _selection,
                };
            }
            else
            {
                Invalidate();
            }
            return;
        }

        if (_dragMode == DragMode.Reselect || _selecting)
        {
            _selecting = false;
            _dragMode = DragMode.None;
            if (_selection.Width < MinSelection || _selection.Height < MinSelection)
            {
                Finish(CaptureOverlayResultKind.Cancelled);
                return;
            }

            Result = new CaptureOverlayResult
            {
                Kind = CaptureOverlayResultKind.Selection,
                Selection = _selection,
            };
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Finish(CaptureOverlayResultKind.Cancelled);
            e.Handled = true;
        }
    }

    public void Finish(CaptureOverlayResultKind kind)
    {
        if (kind == CaptureOverlayResultKind.Cancelled)
            Result = new CaptureOverlayResult { Kind = CaptureOverlayResultKind.Cancelled };
        DialogResult = kind == CaptureOverlayResultKind.Cancelled ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _screen.Dispose();
            _dimmed.Dispose();
            _resultImage?.Dispose();
            _uiFont.Dispose();
            _uiFontItalic.Dispose();
            _toggleLabelFont.Dispose();
            _whiteBrush.Dispose();
            _targetBrush.Dispose();
            _statusBg.Dispose();
            _handleFill.Dispose();
            _toggleOnBg.Dispose();
            _toggleOffBg.Dispose();
            _toggleThumb.Dispose();
            _accentPen.Dispose();
            _handleOutline.Dispose();
            _cardBorder.Dispose();
            _centerFormat.Dispose();
            _cardFormat.Dispose();
        }
        base.Dispose(disposing);
    }

    private Rectangle ClampToForm(Rectangle r)
    {
        var w = Math.Min(r.Width, Width);
        var h = Math.Min(r.Height, Height);
        var x = Math.Clamp(r.X, 0, Math.Max(0, Width - w));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, Height - h));
        return new Rectangle(x, y, w, h);
    }

    private static IEnumerable<Point> EnumerateHandles(Rectangle bounds)
    {
        var midX = bounds.X + bounds.Width / 2;
        var midY = bounds.Y + bounds.Height / 2;
        yield return new Point(bounds.Left, bounds.Top);
        yield return new Point(midX, bounds.Top);
        yield return new Point(bounds.Right, bounds.Top);
        yield return new Point(bounds.Right, midY);
        yield return new Point(bounds.Right, bounds.Bottom);
        yield return new Point(midX, bounds.Bottom);
        yield return new Point(bounds.Left, bounds.Bottom);
        yield return new Point(bounds.Left, midY);
    }

    private static Rectangle HandleRect(Point center)
        => new(center.X - HandleRadius, center.Y - HandleRadius, HandleRadius * 2, HandleRadius * 2);

    private int HitTestHandle(Point p, Rectangle bounds)
    {
        if (bounds.IsEmpty) return -1;
        var i = 0;
        foreach (var h in EnumerateHandles(bounds))
        {
            var hit = HandleRect(h);
            hit.Inflate(3, 3);
            if (hit.Contains(p)) return i;
            i++;
        }
        return -1;
    }

    private static Cursor CursorForHandle(int handle) => handle switch
    {
        0 or 4 => Cursors.SizeNWSE,
        1 or 5 => Cursors.SizeNS,
        2 or 6 => Cursors.SizeNESW,
        3 or 7 => Cursors.SizeWE,
        _ => Cursors.Default,
    };

    private Rectangle ResizeFromHandle(Rectangle start, int handle, Point mouse)
    {
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        switch (handle)
        {
            case 0: left = mouse.X; top = mouse.Y; break;
            case 1: top = mouse.Y; break;
            case 2: right = mouse.X; top = mouse.Y; break;
            case 3: right = mouse.X; break;
            case 4: right = mouse.X; bottom = mouse.Y; break;
            case 5: bottom = mouse.Y; break;
            case 6: left = mouse.X; bottom = mouse.Y; break;
            case 7: left = mouse.X; break;
        }

        var rect = NormalizeRect(new Point(left, top), new Point(right, bottom));
        if (rect.Width < MinSelection) rect.Width = MinSelection;
        if (rect.Height < MinSelection) rect.Height = MinSelection;
        return ClampToForm(rect);
    }

    private static Bitmap CreateDimmed(Bitmap source)
    {
        var bmp = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        using var overlay = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        g.FillRectangle(overlay, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(a.X - b.X);
        var h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }
}
