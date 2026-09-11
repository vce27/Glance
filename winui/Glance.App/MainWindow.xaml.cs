using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Glance.App;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Glance";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { AppWindow.SetIcon(AppIconLoader.IconPath); } catch { /* optional */ }

        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base };

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 304));
        RootFrame.Navigate(typeof(MainPage));

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            AppServices.Current.HideMainWindow();
        };
    }

    /// <summary>
    /// Runtime theme switch: only FrameworkElement.RequestedTheme.
    /// Never touch Application.RequestedTheme after launch (throws 0x80131515).
    /// </summary>
    public void ApplyUiTheme(string? theme)
    {
        var elementTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Dark
            : ElementTheme.Light;
        if (Content is FrameworkElement root)
            root.RequestedTheme = elementTheme;
        if (RootFrame.Content is FrameworkElement page)
            page.RequestedTheme = elementTheme;
    }

    public void SetContentHeight(bool settingsOpen)
    {
        var size = AppWindow.Size;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, settingsOpen ? 720 : 304));
    }

    public void AllowClose() => _allowClose = true;
}
