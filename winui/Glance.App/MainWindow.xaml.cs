using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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
        ApplyCaptionButtonColors(ElementTheme.Light);

        if (Content is FrameworkElement root)
            root.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors(root.ActualTheme);

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
        ApplyCaptionButtonColors(elementTheme);
    }

    /// <summary>
    /// WinUI leaves inactive caption glyphs nearly white on light Mica when
    /// ExtendsContentIntoTitleBar is on — force readable active/inactive colors.
    /// </summary>
    private void ApplyCaptionButtonColors(ElementTheme theme)
    {
        var titleBar = AppWindow.TitleBar;
        var dark = theme == ElementTheme.Dark
            || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        // Active / inactive both need contrast; hover keeps a light wash.
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

    public void SetContentHeight(bool settingsOpen)
    {
        var size = AppWindow.Size;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, settingsOpen ? 720 : 304));
    }

    public void AllowClose() => _allowClose = true;
}
