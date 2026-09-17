using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Glance.App;

/// <summary>Lucide stroke icons (same set morphicons.com uses). Stroke follows host Foreground.</summary>
internal static class LucideStrokeIcon
{
    public const string PinData =
        "M12,17 v5 M9,10.76 a2,2 0 0 1 -1.11,1.79 l-1.78,0.9 A2,2 0 0 0 5,15.24 V16 a1,1 0 0 0 1,1 h12 a1,1 0 0 0 1,-1 v-0.76 a2,2 0 0 0 -1.11,-1.79 l-1.78,-0.9 A2,2 0 0 1 15,10.76 V7 a1,1 0 0 1 1,-1 a2,2 0 0 0 0,-4 H8 a2,2 0 0 0 0,4 a1,1 0 0 1 1,1 z";

    public const string ScanTextData =
        "M3,7 V5 A2,2 0 0 1 5,3 H7 M17,3 H19 A2,2 0 0 1 21,5 V7 M21,17 V19 A2,2 0 0 1 19,21 H17 M7,21 H5 A2,2 0 0 1 3,19 V17 M7,8 H15 M7,12 H17 M7,16 H13";

    public const string Volume2Data =
        "M11,4.702 a0.705,0.705 0 0 0 -1.203,-0.498 L6.413,7.587 A1.4,1.4 0 0 1 5.416,8 H3 a1,1 0 0 0 -1,1 v6 a1,1 0 0 0 1,1 h2.416 a1.4,1.4 0 0 1 0.997,0.413 l3.383,3.384 A0.705,0.705 0 0 0 11,19.298 z M16,9 a5,5 0 0 1 0,6 M19.364,18.364 a9,9 0 0 0 0,-12.728";

    public const string SquareData =
        "M5,5 H19 V19 H5 Z";

    public const string SettingsData =
        "M12.22,2 h-0.44 a2,2 0 0 0 -2,2 v0.18 a2,2 0 0 1 -1,1.73 l-0.43,0.25 a2,2 0 0 1 -2,0 l-0.15,-0.08 a2,2 0 0 0 -2.73,0.73 l-0.22,0.38 a2,2 0 0 0 0.73,2.73 l0.15,0.1 a2,2 0 0 1 1,1.72 v0.51 a2,2 0 0 1 -1,1.74 l-0.15,0.09 a2,2 0 0 0 -0.73,2.73 l0.22,0.38 a2,2 0 0 0 2.73,0.73 l0.15,-0.08 a2,2 0 0 1 2,0 l0.43,0.25 a2,2 0 0 1 1,1.73 V20 a2,2 0 0 0 2,2 h0.44 a2,2 0 0 0 2,-2 v-0.18 a2,2 0 0 1 1,-1.73 l0.43,-0.25 a2,2 0 0 1 2,0 l0.15,0.08 a2,2 0 0 0 2.73,-0.73 l0.22,-0.39 a2,2 0 0 0 -0.73,-2.73 l-0.15,-0.08 a2,2 0 0 1 -1,-1.74 v-0.5 a2,2 0 0 1 1,-1.74 l0.15,-0.09 a2,2 0 0 0 0.73,-2.73 l-0.22,-0.38 a2,2 0 0 0 -2.73,-0.73 l-0.15,0.08 a2,2 0 0 1 -2,0 l-0.43,-0.25 a2,2 0 0 1 -1,-1.73 V4 a2,2 0 0 0 -2,-2 z M12,15 a3,3 0 1 0 0,-6 a3,3 0 1 0 0,6 z";

    public static Viewbox Create(string pathData, double size = 16)
    {
        var path = (ShapePath)XamlReader.Load(
            $"""
            <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Width="24" Height="24" Stretch="Uniform" Fill="Transparent"
                  Stroke="Black" StrokeThickness="2"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  Data="{pathData}" />
            """);

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = path,
            Tag = path,
        };
    }

    /// <summary>Keep stroke in sync with the button ContentPresenter Foreground (hover / checked).</summary>
    public static void BindToControlForeground(Control host, Viewbox icon)
    {
        if (icon.Tag is not ShapePath path) return;

        void Sync()
        {
            var presenter = FindDescendant<ContentPresenter>(host);
            var brush = presenter?.Foreground as Brush ?? host.Foreground;
            if (brush is not null)
                path.Stroke = brush;
        }

        void Attach()
        {
            Sync();
            var presenter = FindDescendant<ContentPresenter>(host);
            presenter?.RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (_, _) => Sync());
        }

        if (host.IsLoaded)
            Attach();
        else
            host.Loaded += (_, _) => Attach();

        host.ActualThemeChanged += (_, _) => Sync();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
