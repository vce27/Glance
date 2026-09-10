using Microsoft.UI.Xaml;

namespace Glance.App;

public partial class App : Application
{
    private readonly AppServices _services = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_services.TryTakeSingleInstance())
        {
            // Second instance: exit; primary stays in tray.
            Exit();
            return;
        }

        var startMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, AppServices.SilentStartArg, StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        _services.InitializeShell(_window, startMinimized);
        if (!startMinimized)
            _window.Activate();
    }
}
