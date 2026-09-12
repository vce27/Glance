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

        host.Loaded += (_, _) =>
        {
            Sync();
            var presenter = FindDescendant<ContentPresenter>(host);
            presenter?.RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (_, _) => Sync());
        };
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
