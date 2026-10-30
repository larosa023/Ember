using Avalonia;
using Ember.Services;
using Velopack;

namespace Ember;

internal static class Program
{
    /// <summary>True when launched with --minimized (e.g. at Windows sign-in): start in the tray.</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>
    /// --smoke-test: used by the release build. Ember opens, visits every page, then exits 0.
    /// Any crash exits non-zero (and never shows a dialog), which blocks the release.
    /// </summary>
    public static bool SmokeTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: lets Velopack handle install/update/uninstall hooks.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AutoStart.RemoveAll())
            .Run();

        StartMinimized = args.Contains("--minimized");
        SmokeTest = args.Contains("--smoke-test");

        // Only one Ember at a time. A second launch just brings the first one forward.
        // "--restarted" (Restart as admin) waits for the old instance to exit instead.
        if (!SmokeTest && !SingleInstance.TryAcquire(waitForPrevious: args.Contains("--restarted")))
            return 0;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.ExceptionObject as Exception);
            if (SmokeTest) Environment.Exit(3);
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            if (!SmokeTest) CrashLog.ShowMessage(ex);
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
