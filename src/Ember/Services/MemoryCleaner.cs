using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>
/// "Clear standby memory": drops the standby list (file cache Windows keeps around just in case).
/// Harmless — Windows rebuilds it as needed — and it's what tools like ISLC do to smooth out stutters
/// when RAM is nearly full. Needs admin.
/// </summary>
public static class MemoryCleaner
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    /// <summary>Bytes currently on the standby list, or null if Windows won't tell us (not admin).</summary>
    public static long? StandbyBytes()
    {
        if (!Environment.IsPrivilegedProcess || IntPtr.Size != 8) return null;
        EnablePrivilege();
        // SYSTEM_MEMORY_LIST_INFORMATION: 5 counters, then PageCountByPriority[8] (the standby list), then 9 more.
        const int fields = 5 + 8 + 8 + 1;
        var buffer = new long[fields];
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buffer, fields * 8, out _) != 0) return null;
        }
        catch { return null; }
        long pages = 0;
        for (int i = 5; i < 13; i++) pages += buffer[i];
        return pages * Environment.SystemPageSize;
    }

    /// <summary>Returns null on success, otherwise a message.</summary>
    public static string? ClearStandby()
    {
        if (!Environment.IsPrivilegedProcess) return "Clearing standby memory needs admin. Use \"Restart as admin\".";
        if (!EnablePrivilege()) return "Windows didn't allow it.";
        int command = MemoryPurgeStandbyList;
        try
        {
            var rc = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            return rc == 0 ? null : $"Windows refused (0x{rc:X8}).";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static bool _privilege;

    private static bool EnablePrivilege()
    {
        if (_privilege) return true;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 /* ADJUST_PRIVILEGES */ | 0x0008 /* QUERY */, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 /* ENABLED */ };
            _privilege = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) &&
                         Marshal.GetLastWin32Error() == 0;
            return _privilege;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, long[] buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state,
        int bufferLength, IntPtr previous, IntPtr returnLength);
}
