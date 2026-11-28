using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Glance.Capture;

public sealed class MonitorInfo
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class MonitorSnapshot
{
    public required MonitorInfo Monitor { get; init; }
    public required Bitmap Bitmap { get; init; }
}

public static class MonitorCapture
{
    public static MonitorInfo FindCursorMonitor()
    {
        var b = Screen.FromPoint(Cursor.Position).Bounds;
        return new MonitorInfo
        {
            X = b.X,
            Y = b.Y,
            Width = b.Width,
            Height = b.Height,
        };
    }

    public static MonitorSnapshot CaptureMonitor(MonitorInfo monitor)
    {
        var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(monitor.X, monitor.Y, 0, 0, new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
        }

        return new MonitorSnapshot
        {
            Monitor = monitor,
            Bitmap = bmp,
        };
    }

    public static byte[] CropToPng(Bitmap source, Rectangle rect)
    {
        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, source.Width, source.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("empty crop rect");

        using var crop = source.Clone(rect, PixelFormat.Format32bppArgb);
        using var ms = new MemoryStream();
        crop.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Grab a screen rectangle (screen coords) as PNG — used for terminal 划词 OCR.</summary>
    public static byte[] CaptureRegionPng(int x0, int y0, int x1, int y1, int pad = 6)
    {
        var left = Math.Min(x0, x1) - pad;
        var top = Math.Min(y0, y1) - pad;
        var right = Math.Max(x0, x1) + pad;
        var bottom = Math.Max(y0, y1) + pad;
        // Single-line selections are often flat — ensure a readable band for OCR.
        if (bottom - top < 28)
        {
            var mid = (top + bottom) / 2;
            top = mid - 14;
            bottom = mid + 14;
        }
        if (right - left < 24) right = left + 24;

        var w = Math.Max(1, right - left);
        var h = Math.Max(1, bottom - top);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(left, top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
