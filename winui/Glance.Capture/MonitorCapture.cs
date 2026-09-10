using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glance.Capture;

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double ScaleFactor { get; init; } = 1.0;
}

public sealed class MonitorSnapshot
{
    public required MonitorInfo Monitor { get; init; }
    public required Bitmap Bitmap { get; init; }
    public required byte[] PngBytes { get; init; }
}

public static class MonitorCapture
{
    public static MonitorInfo FindCursorMonitor()
    {
        if (!GetCursorPos(out var pt))
            throw new InvalidOperationException("GetCursorPos failed");

        MonitorInfo? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return true;

                var r = info.rcMonitor;
                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                {
                    var dpi = GetDpi(hMonitor);
                    found = new MonitorInfo
                    {
                        Handle = hMonitor,
                        X = r.Left,
                        Y = r.Top,
                        Width = r.Right - r.Left,
                        Height = r.Bottom - r.Top,
                        ScaleFactor = dpi / 96.0,
                    };
                    return false;
                }
                return true;
            }, IntPtr.Zero);

        if (found is null)
            throw new InvalidOperationException($"No monitor at cursor ({pt.X},{pt.Y})");
        return found;
    }

    public static MonitorSnapshot CaptureMonitor(MonitorInfo monitor)
    {
        // Physical-pixel BitBlt of the cursor monitor only (mixed-DPI safe).
        var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(monitor.X, monitor.Y, 0, 0, new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new MonitorSnapshot
        {
            Monitor = monitor,
            Bitmap = bmp,
            PngBytes = ms.ToArray(),
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

    private static uint GetDpi(IntPtr hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
                return dpiX;
        }
        catch
        {
            // older OS
        }
        return 96;
    }

    private const int MDT_EFFECTIVE_DPI = 0;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
