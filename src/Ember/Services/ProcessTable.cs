using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>One process as reported by the kernel's process list.</summary>
internal readonly record struct RawProcess(
    int Pid, string Name, long CpuTime, long PrivateWorkingSet, long IoBytes, bool Suspended);

/// <summary>
/// Every running process in a single NtQuerySystemInformation call: name, CPU time, private
/// working set (Task Manager's Memory number), I/O byte totals and thread states. This replaces
/// opening a handle to each process every second. 64-bit layouts (see phnt / Process Hacker).
/// </summary>
internal sealed class ProcessTable : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    // SYSTEM_PROCESS_INFORMATION (x64)
    private const int OffNumberOfThreads = 4;
    private const int OffWorkingSetPrivate = 8;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLength = 56;
    private const int OffImageNameBuffer = 64;
    private const int OffProcessId = 80;
    private const int OffReadTransfer = 232;
    private const int OffWriteTransfer = 240;
    private const int OffThreads = 256;

    // SYSTEM_THREAD_INFORMATION (x64)
    private const int ThreadSize = 80;
    private const int OffThreadState = 68;
    private const int OffWaitReason = 72;

    private IntPtr _buffer;
    private int _size = 1 << 20;   // reused between calls; grows when needed

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private List<RawProcess> _last = new();

    /// <summary>The process table; on a (rare) failed read, the previous one so the list doesn't blink empty.</summary>
    public List<RawProcess> Read()
    {
        var list = new List<RawProcess>(400);
        if (IntPtr.Size != 8) return list;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_size);

            var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out var needed);
            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _size = Math.Max(_size * 2, needed + 256 * 1024);
                continue;
            }
            if (status != 0) return _last;

            long offset = 0;
            while (offset + OffThreads <= _size)
            {
                var entry = _buffer + (nint)offset;
                var next = (uint)Marshal.ReadInt32(entry, 0);
                var pid = (int)Marshal.ReadIntPtr(entry, OffProcessId);

                if (pid != 0)   // skip "System Idle Process"
                {
                    var threads = Marshal.ReadInt32(entry, OffNumberOfThreads);
                    var nameLength = (ushort)Marshal.ReadInt16(entry, OffImageNameLength);
                    var namePtr = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
                    var name = namePtr == IntPtr.Zero || nameLength == 0 ? "" : Marshal.PtrToStringUni(namePtr, nameLength / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

                    bool suspended = threads > 0 && pid > 4;
                    for (int t = 0; suspended && t < threads; t++)
                    {
                        var thread = entry + OffThreads + t * ThreadSize;
                        if (Marshal.ReadInt32(thread, OffThreadState) != 5 /* Waiting */ ||
                            Marshal.ReadInt32(thread, OffWaitReason) != 5 /* Suspended */)
                            suspended = false;
                    }

                    list.Add(new RawProcess(
                        pid,
                        name.Length == 0 ? (pid == 4 ? "System" : $"Process {pid}") : name,
                        Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime),
                        Marshal.ReadInt64(entry, OffWorkingSetPrivate),
                        Marshal.ReadInt64(entry, OffReadTransfer) + Marshal.ReadInt64(entry, OffWriteTransfer),
                        suspended));
                }

                if (next == 0) break;
                offset += next;
            }
            return _last = list;
        }
        return _last;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
