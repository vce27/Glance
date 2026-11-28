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
}
