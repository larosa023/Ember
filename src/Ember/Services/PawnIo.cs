using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Ember.Services;

/// <summary>
/// PawnIO is the signed kernel driver LibreHardwareMonitor uses to read CPU temperatures,
/// motherboard sensors and fan headers (it replaced WinRing0, which Windows Defender blocks).
/// It is installed separately, once per PC.
/// </summary>
public static class PawnIo
{
    public const string DownloadPage = "https://github.com/namazso/PawnIO.Setup/releases/latest";

    public static bool IsInstalled
    {
        get
        {
            try
            {
                using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                    if (service is not null) return true;

                var driver = Path.Combine(Environment.SystemDirectory, "drivers", "PawnIO.sys");
                if (File.Exists(driver)) return true;

                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Directory.Exists(Path.Combine(programFiles, "PawnIO"));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Installs PawnIO with winget (one UAC prompt). Falls back to opening the download page.
    /// Returns true when the driver is present afterwards.
    /// </summary>
    public static async Task<bool> InstallAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Minimized,
            };
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync();
        }
        catch (Win32Exception)
        {
            return false;   // UAC prompt was cancelled
        }
        catch
        {
            // winget missing or failed: fall through to the manual download.
        }

        if (IsInstalled) return true;

        try { Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true }); } catch { }
        return false;
    }
}
