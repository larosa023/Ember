using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Ember.Services;

/// <summary>
/// Launch-at-sign-in, two ways:
///  - Normal: a HKCU Run entry. No admin needed, but CPU temps stay hidden.
///  - With admin: a Task Scheduler task set to "run with highest privileges", so Ember
///    starts elevated at sign-in with no UAC prompt. Creating it requires Ember to be elevated once.
/// Both launch with --minimized so Ember starts quietly in the tray.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ember";
    private const string TaskName = "Ember (start with admin)";

    private static string ExePath => Environment.ProcessPath ?? "";
    private static string Command => $"\"{ExePath}\" --minimized";

    public static bool RunKeyEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static bool TaskExists => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Creates or deletes the elevated logon task. Requires Ember to be running as admin.</summary>
    public static bool SetTask(bool enabled)
    {
        if (!enabled) return Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var exe = SecurityElement.Escape(ExePath);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Ember in the tray at sign-in with admin rights (for CPU temperatures).</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{exe}</Command><Arguments>--minimized</Arguments></Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "ember-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>If Ember moved (e.g. after an update), point the Run entry at the current exe.</summary>
    public static void RefreshPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && current != Command)
                key.SetValue(ValueName, Command);
        }
        catch { }
    }

    /// <summary>Called by the uninstaller.</summary>
    public static void RemoveAll()
    {
        try { SetRunKey(false); } catch { }
        try { if (TaskExists) SetTask(false); } catch { }
    }

    private static int Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10_000) ? p.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
