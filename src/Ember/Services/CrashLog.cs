using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>Writes startup/unhandled crashes to %AppData%\Ember\crash.log so they can be diagnosed.</summary>
public static class CrashLog
{
    public static readonly string FilePath = Path.Combine(SettingsService.Dir, "crash.log");

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(SettingsService.Dir);
            File.AppendAllText(FilePath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Ember {AppInfo.Version} ===={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Native message box, since the UI may not exist yet when this runs.</summary>
    public static void ShowMessage(Exception ex)
    {
        try
        {
            MessageBoxW(IntPtr.Zero,
                $"Ember couldn't start.\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails were saved to:\n{FilePath}",
                "Ember", 0x10 /* MB_ICONERROR */);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
