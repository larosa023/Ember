using Microsoft.Win32;

namespace Ember.Services;

public sealed record StartupEntry(
    string Name,
    string ValueName,
    string Command,
    string Location,
    bool Machine,
    string ApprovedKey,
    bool Enabled);

/// <summary>
/// Lists and toggles startup apps exactly the way Windows Task Manager does
/// (the Run keys, the Startup folders, and the "StartupApproved" flags).
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static List<StartupEntry> Load()
    {
        var list = new List<StartupEntry>();
        ReadRun(machine: false, "Current user", list);
        ReadRun(machine: true, "All users", list);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), machine: false, "Startup folder", list);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), machine: true, "Startup folder (all users)", list);
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        var root = entry.Machine ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.CreateSubKey(entry.ApprovedKey, writable: true);

        // 12 bytes: first byte 0x02 = enabled, 0x03 = disabled; bytes 4-11 = FILETIME when disabled.
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(entry.ValueName, data, RegistryValueKind.Binary);
    }

    private static void ReadRun(bool machine, string location, List<StartupEntry> into)
    {
        var root = machine ? Registry.LocalMachine : Registry.CurrentUser;
        using var run = root.OpenSubKey(RunKey);
        if (run is null) return;

        foreach (var name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var command = run.GetValue(name)?.ToString() ?? "";
            into.Add(new StartupEntry(name, name, command, location, machine, ApprovedRun,
                IsEnabled(root, ApprovedRun, name)));
        }
    }

    private static void ReadFolder(string folder, bool machine, string location, List<StartupEntry> into)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var root = machine ? Registry.LocalMachine : Registry.CurrentUser;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            into.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), fileName, file, location, machine,
                ApprovedFolder, IsEnabled(root, ApprovedFolder, fileName)));
        }
    }

    private static bool IsEnabled(RegistryKey root, string approvedKey, string valueName)
    {
        using var key = root.OpenSubKey(approvedKey);
        if (key?.GetValue(valueName) is byte[] { Length: > 0 } data)
            return (data[0] & 1) == 0;  // even = enabled, odd = disabled
        return true;
    }
}
