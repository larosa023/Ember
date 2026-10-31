using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>
/// Finds suspended processes (every thread waiting with reason "Suspended"), like Task Manager's
/// pause icon. One NtQuerySystemInformation call covers every process. 64-bit layouts.
/// </summary>
internal static class SuspendedProcesses
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const int ProcessIdOffset = 0x50;
    private const int ThreadsOffset = 0x100;
    private const int ThreadSize = 0x50;
    private const int ThreadStateOffset = 68;
    private const int WaitReasonOffset = 72;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public static HashSet<int> Find()
    {
        var result = new HashSet<int>();
        if (IntPtr.Size != 8) return result;

        int size = 1 << 20;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 65536);
                    continue;
                }
                if (status != 0) return result;

                var offset = 0L;
                while (true)
                {
                    var entry = buffer + (nint)offset;
                    var next = (uint)Marshal.ReadInt32(entry, 0);
                    var threads = Marshal.ReadInt32(entry, 4);
                    var pid = (int)Marshal.ReadIntPtr(entry, ProcessIdOffset);

                    if (pid > 4 && threads > 0)
                    {
                        bool allSuspended = true;
                        for (int t = 0; t < threads; t++)
                        {
                            var thread = entry + ThreadsOffset + t * ThreadSize;
                            var state = Marshal.ReadInt32(thread, ThreadStateOffset);
                            var reason = Marshal.ReadInt32(thread, WaitReasonOffset);
                            if (state != 5 /* Waiting */ || reason != 5 /* Suspended */)
                            {
                                allSuspended = false;
                                break;
                            }
                        }
                        if (allSuspended) result.Add(pid);
                    }

                    if (next == 0) break;
                    offset += next;
                    if (offset >= size) break;
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }
}
