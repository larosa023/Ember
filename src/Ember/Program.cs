using Avalonia;
using Ember.Services;
using Velopack;

namespace Ember;

internal static class Program
{
    /// <summary>True when launched with --minimized (e.g. at Windows sign-in): start in the tray.</summary>
    public static bool StartMinimized { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: lets Velopack handle install/update/uninstall hooks.
        VelopackApp.Build()
            .WithBeforeUninstallFastCallback(_ => AutoStart.RemoveAll())
            .Run();

        StartMinimized = args.Contains("--minimized");

        // Only one Ember at a time. A second launch just brings the first one forward.
        // "--restarted" (Restart as admin) waits for the old instance to exit instead.
        if (!SingleInstance.TryAcquire(waitForPrevious: args.Contains("--restarted")))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
