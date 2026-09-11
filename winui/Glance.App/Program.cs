using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Glance.App;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        // Must run before any WinUI / COM init so update hooks stay fast.
        VelopackApp.Build().SetArgs(args).Run();

        if (args.Any(a => string.Equals(a, "--self-test-update", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunSelfTestUpdateAsync(args).GetAwaiter().GetResult());
            return;
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <summary>
    /// Headless update probe for CI/local testing.
    /// Exit 0 = update available (and applied if --apply), 1 = up to date, 2 = not installed / error.
    /// Optional: GLANCE_UPDATE_SOURCE or --update-source &lt;dir&gt;
    /// </summary>
    private static async Task<int> RunSelfTestUpdateAsync(string[] args)
    {
        string? source = Environment.GetEnvironmentVariable("GLANCE_UPDATE_SOURCE");
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--update-source", StringComparison.OrdinalIgnoreCase))
                source = args[i + 1];
        }

        var apply = args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));
        var updates = new AppUpdateService(source);
        Console.WriteLine($"installed={updates.IsInstalled} version={updates.CurrentVersionDisplay} source={source ?? "github"}");

        var check = await updates.CheckAsync();
        Console.WriteLine($"check={check.Kind} {check.Message}");
        if (check.Kind == UpdateCheckKind.UpToDate)
            return 1;
        if (check.Kind != UpdateCheckKind.UpdateAvailable)
            return 2;

        if (!apply)
            return 0;

        var progress = new Progress<int>(p => Console.WriteLine($"download={p}%"));
        var result = await updates.DownloadAndApplyAsync(progress);
        Console.WriteLine(result.Message);
        // ApplyUpdatesAndRestart normally exits; if we return, it failed.
        return result.Started ? 0 : 2;
    }
}
