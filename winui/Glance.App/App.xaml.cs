using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace Glance.App;

public partial class App : Application
{
    private readonly AppServices _services = new();
    private MainWindow? _window;

    public App()
    {
        // Application.RequestedTheme may only be set before windows exist.
        // Load theme early so we set it once here — never again after launch.
        try
        {
            var theme = _services.Store.LoadSettings().UiTheme;
            RequestedTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch
        {
            RequestedTheme = ApplicationTheme.Light;
        }

        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Debug.WriteLine("Unhandled: " + e.Exception);
            try
            {
                var log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Glance", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"[{DateTime.Now:o}] {e.Exception}\n\n");
            }
            catch { /* ignore */ }
            e.Handled = true;
        };
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
        _window = new MainWindow();
        _services.InitializeShell(_window, startMinimized);
        _window.ApplyUiTheme(settings.UiTheme);
        if (!startMinimized)
            _window.Activate();
    }
}
