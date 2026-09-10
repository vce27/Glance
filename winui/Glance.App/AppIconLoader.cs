using System.Runtime.InteropServices;

namespace Glance.App;

internal static class AppIconLoader
{
    public static string IconPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Assets", "AppIcon.ico"),
                Path.Combine(baseDir, "AppIcon.ico"),
            };
            return candidates.FirstOrDefault(File.Exists)
                   ?? Path.Combine(baseDir, "Assets", "AppIcon.ico");
        }
    }

    /// <summary>HICON for tray / Win32. Destroy with DestroyIcon when finished.</summary>
    public static IntPtr LoadTrayIconHandle()
    {
        var path = IconPath;
        if (File.Exists(path))
        {
            var fromFile = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            if (fromFile != IntPtr.Zero) return fromFile;
        }

        // Unpackaged publish embeds ApplicationIcon into the EXE — extract that.
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            var large = IntPtr.Zero;
            var small = IntPtr.Zero;
            var count = ExtractIconEx(exe, 0, ref large, ref small, 1);
            if (count > 0)
            {
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return small;
                }
                if (large != IntPtr.Zero) return large;
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION fallback
    }

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, ref IntPtr phiconLarge, ref IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
