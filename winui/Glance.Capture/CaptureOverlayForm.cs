using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
    private readonly Bitmap _screen;
    private readonly Bitmap _dimmed;
    private Point? _start;
    private Rectangle _selection;
    private bool _selecting;
    private Bitmap? _resultImage;
    private bool _showingResult;
    private bool _showOriginal;
    private byte[]? _translatedJpeg;
    private string? _statusText;

    public CaptureOverlayResult? Result { get; private set; }

    public CaptureOverlayForm(MonitorSnapshot snapshot)
    {
        _screen = (Bitmap)snapshot.Bitmap.Clone();
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
        BackgroundImage = _dimmed;
        BackgroundImageLayout = ImageLayout.None;

        // Pre-paint before visible (Windows skips WM_SIZE while invisible).
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void ShowLoading(string text = "翻译中…")
    {
        _statusText = text;
        _showingResult = false;
        Invalidate();
        Update();
    }

    public void ShowTranslatedResult(byte[] jpegBytes)
    {
        _translatedJpeg = jpegBytes;
        using var ms = new MemoryStream(jpegBytes);
        _resultImage?.Dispose();
        _resultImage = new Bitmap(ms);
        _showingResult = true;
        _showOriginal = false;
        _statusText = null;
        Cursor = Cursors.Default;
        Invalidate();
        Update();
    }

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

        if (_showingResult && _resultImage is not null && !_selection.IsEmpty)
        {
            g.DrawImage(_dimmed, 0, 0, Width, Height);
            var img = _showOriginal ? _screen : _resultImage;
            var src = _showOriginal
                ? _selection
                : new Rectangle(0, 0, img.Width, img.Height);
            g.DrawImage(img, _selection, src, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.FromArgb(0, 120, 212), 2);
            g.DrawRectangle(pen, _selection);
            return;
        }

        g.DrawImage(_dimmed, 0, 0, Width, Height);
        if (!_selection.IsEmpty)
        {
            g.SetClip(_selection, CombineMode.Replace);
            g.DrawImage(_screen, 0, 0, Width, Height);
            g.ResetClip();
            using var pen = new Pen(Color.FromArgb(0, 120, 212), 2);
            g.DrawRectangle(pen, _selection);
        }

        if (!string.IsNullOrEmpty(_statusText))
        {
            var rect = _selection.IsEmpty
                ? new Rectangle(Width / 2 - 80, Height / 2 - 20, 160, 40)
                : new Rectangle(_selection.X, Math.Max(0, _selection.Y - 36), Math.Max(120, _selection.Width), 32);
            using var bg = new SolidBrush(Color.FromArgb(200, 32, 32, 32));
            using var fg = new SolidBrush(Color.White);
            g.FillRectangle(bg, rect);
            using var font = new Font("Segoe UI", 11f);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_statusText, font, fg, rect, sf);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_showingResult)
        {
            if (e.Button == MouseButtons.Right)
            {
                _showOriginal = !_showOriginal;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left)
            {
                Finish(CaptureOverlayResultKind.Cancelled);
            }
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            Finish(CaptureOverlayResultKind.Cancelled);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _selecting = true;
            _start = e.Location;
            _selection = new Rectangle(e.Location, Size.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_selecting || _start is null) return;
        var a = _start.Value;
        var b = e.Location;
        _selection = NormalizeRect(a, b);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_selecting || e.Button != MouseButtons.Left) return;
        _selecting = false;
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            Finish(CaptureOverlayResultKind.Cancelled);
            return;
        }
        Result = new CaptureOverlayResult
        {
            Kind = CaptureOverlayResultKind.Selection,
            Selection = _selection,
        };
        // Keep overlay open for loading/result; caller drives next steps.
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
        }
        base.Dispose(disposing);
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
