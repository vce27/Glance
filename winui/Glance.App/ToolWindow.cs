using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace Glance.App;

/// <summary>Shared chrome for settings / wordbook satellite windows.</summary>
internal static class ToolWindow
{
    public static void Configure(Window window, string title, double widthDip, double heightDip)
    {
        window.Title = title;
        window.ExtendsContentIntoTitleBar = true;
        window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base };
        try { window.AppWindow.SetIcon(AppIconLoader.IconPath); } catch { /* optional */ }

        if (window.Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                var scale = root.XamlRoot?.RasterizationScale ?? 1.0;
                var w = (int)Math.Ceiling(widthDip * scale);
                var h = (int)Math.Ceiling(heightDip * scale);
                try { window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h)); }
                catch { window.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h)); }
            };
            root.ActualThemeChanged += (_, _) => ApplyCaptionColors(window, root.ActualTheme);
            ApplyCaptionColors(window, root.ActualTheme);
        }
    }

    public static void Activate(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        try { window.AppWindow.Show(); } catch { /* ignore */ }
        window.Activate();
        SetForegroundWindow(hwnd);
    }

    public static void ApplyCaptionColors(Window window, ElementTheme theme)
    {
        var titleBar = window.AppWindow.TitleBar;
        var dark = theme == ElementTheme.Dark
            || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        Color fg = dark ? Color.FromArgb(255, 202, 202, 202) : Color.FromArgb(255, 50, 50, 50);
        Color fgInactive = dark ? Color.FromArgb(255, 140, 140, 140) : Color.FromArgb(255, 96, 96, 96);
        Color hoverBg = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
        Color pressBg = dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(45, 0, 0, 0);
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonInactiveForegroundColor = fgInactive;
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonPressedForegroundColor = fg;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = hoverBg;
        titleBar.ButtonPressedBackgroundColor = pressBg;
    }

    public static void ApplyTheme(Window? window, string? theme)
    {
        if (window?.Content is not FrameworkElement root) return;
        var elementTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Dark
            : ElementTheme.Light;
        root.RequestedTheme = elementTheme;
        ApplyCaptionColors(window, elementTheme);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
