using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace Ember.Services;

public sealed record ServiceInfo(
    string Name, string DisplayName, string Description,
    string State, string StartMode, bool Delayed, int Pid, bool IsMicrosoft);

/// <summary>
/// Lists and controls Windows services through WMI (Win32_Service). Reading works without admin;
/// starting, stopping and changing the startup type need admin. Call from a background thread.
/// </summary>
public static class WindowsServices
{
    /// <summary>Stopping or disabling these can break Windows or log you out, so Ember refuses.</summary>
    public static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        // Restarting things like Windows Audio is a common fix, so only truly core services are listed.
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "BrokerInfrastructure", "Power", "PlugPlay",
        "ProfSvc", "Schedule", "gpsvc", "CoreMessagingRegistrar", "SystemEventsBroker", "StateRepository",
        "Winmgmt", "EventLog", "nsi", "BFE", "mpssvc", "UserManager",
    };

    private static readonly Dictionary<string, bool> CompanyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public static List<ServiceInfo> Load()
    {
        var list = new List<ServiceInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, Description, State, StartMode, ProcessId, PathName FROM Win32_Service");

        foreach (var o in searcher.Get())
        {
            using var s = (ManagementObject)o;
            var name = s["Name"]?.ToString() ?? "";
            if (name.Length == 0) continue;

            var mode = s["StartMode"]?.ToString() ?? "";
            int pid = 0;
            try { pid = Convert.ToInt32(s["ProcessId"] ?? 0); } catch { }

            list.Add(new ServiceInfo(
                name,
                s["DisplayName"]?.ToString() ?? name,
                s["Description"]?.ToString() ?? "",
                s["State"]?.ToString() ?? "Unknown",
                mode == "Auto" ? "Automatic" : mode,
                mode == "Auto" && IsDelayed(name),
                pid,
                IsMicrosoft(s["PathName"]?.ToString())));
        }

        return list.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string Start(string name) => Invoke(name, "StartService");

    public static string Stop(string name) =>
        Critical.Contains(name) ? "This is a core Windows service, so Ember won't stop it." : Invoke(name, "StopService");

    public static string Restart(string name)
    {
        var stop = Stop(name);
        if (stop.Length > 0) return stop;

        // Wait up to 15 s for it to actually stop before starting again.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && State(name) is not "Stopped")
            Thread.Sleep(300);
        return Start(name);
    }

    /// <summary>mode: "auto", "delayed", "manual" or "disabled".</summary>
    public static string SetStartup(string name, string mode)
    {
        if (mode == "disabled" && Critical.Contains(name))
            return "This is a core Windows service, so Ember won't disable it.";

        var wmiMode = mode switch
        {
            "auto" or "delayed" => "Automatic",
            "disabled" => "Disabled",
            _ => "Manual",
        };
        var result = Invoke(name, "ChangeStartMode", wmiMode);
        if (result.Length > 0) return result;

        if (mode is "auto" or "delayed")
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
                key?.SetValue("DelayedAutostart", mode == "delayed" ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException)
            {
                return "Needs admin. Use \"Restart as admin\".";
            }
        }
        return "";
    }

    private static string? State(string name)
    {
        try
        {
            using var s = new ManagementObject($"Win32_Service.Name='{Escape(name)}'");
            s.Get();
            return s["State"]?.ToString();
        }
        catch { return null; }
    }

    /// <summary>Runs a Win32_Service method. Returns "" on success, otherwise a friendly message.</summary>
    private static string Invoke(string name, string method, params object[] args)
    {
        try
        {
            using var s = new ManagementObject($"Win32_Service.Name='{Escape(name)}'");
            var code = Convert.ToInt32(s.InvokeMethod(method, args.Length == 0 ? null : args) ?? 0);
            return code switch
            {
                0 or 10 => "",   // 10 = already running
                2 => "Needs admin. Use \"Restart as admin\".",
                3 => "Other running services depend on this one. Stop them first.",
                5 or 6 => "The service isn't accepting that request right now.",
                7 => "The service didn't respond in time.",
                14 => "The service is disabled. Set its startup type to Manual or Automatic first.",
                15 => "The service couldn't log on (its account settings are wrong).",
                21 => "Invalid startup type.",
                _ => $"Windows refused (code {code}).",
            };
        }
        catch (UnauthorizedAccessException)
        {
            return "Needs admin. Use \"Restart as admin\".";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool IsDelayed(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("DelayedAutostart") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>svchost-hosted or signed "Microsoft" in the exe's version info.</summary>
    private static bool IsMicrosoft(string? pathName)
    {
        var exe = ExePath(pathName);
        if (exe is null) return false;
        if (exe.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase)) return true;

        lock (CompanyCache)
        {
            if (CompanyCache.TryGetValue(exe, out var cached)) return cached;
            bool result;
            try
            {
                result = File.Exists(exe)
                    ? FileVersionInfo.GetVersionInfo(exe).CompanyName?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true
                    : exe.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                result = exe.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);
            }
            CompanyCache[exe] = result;
            return result;
        }
    }

    private static string? ExePath(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName)) return null;
        var p = Environment.ExpandEnvironmentVariables(pathName.Trim());
        if (p.StartsWith('"'))
        {
            var end = p.IndexOf('"', 1);
            return end > 1 ? p[1..end] : null;
        }
        var exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? p[..(exe + 4)] : p.Split(' ')[0];
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
