using Microsoft.UI.Xaml;

namespace Glance.App;

public partial class App : Application
{
    private readonly AppServices _services = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // Default light; actual theme applied after settings load.
        RequestedTheme = ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_services.TryTakeSingleInstance())
        {
            Exit();
            return;
        }

        var startMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, AppServices.SilentStartArg, StringComparison.OrdinalIgnoreCase));

        var settings = _services.Store.LoadSettings();
        ApplyAppTheme(settings.UiTheme);

        _window = new MainWindow();
        _services.InitializeShell(_window, startMinimized);
        _window.ApplyUiTheme(settings.UiTheme);
        if (!startMinimized)
            _window.Activate();
    }

    public static void ApplyAppTheme(string? theme)
    {
        if (Current is not App) return;
        Current.RequestedTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
    }
}
