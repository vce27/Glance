using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

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
        try { AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { /* optional */ }

        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base };

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 420));
        RootFrame.Navigate(typeof(MainPage));

        // Close → tray (hide), unless quitting.
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            AppServices.Current.HideMainWindow();
        };
    }

    public void AllowClose() => _allowClose = true;
}
