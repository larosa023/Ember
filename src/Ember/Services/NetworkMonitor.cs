using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>
/// Per-process network bytes using Windows' own event tracing (ETW, provider
/// Microsoft-Windows-Kernel-Network), the same source Resource Monitor uses. Needs admin.
/// Runs a private real-time session on a background thread only while it's needed.
/// </summary>
internal sealed class NetworkMonitor : IDisposable
{
    private const string SessionName = "Ember-Network";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Event IDs: TCPv4, TCPv6, UDPv4, UDPv6 (send, receive). Payload starts with PID, size (both UInt32).
    private static readonly HashSet<ushort> SendIds = [10, 26, 42, 58];
    private static readonly HashSet<ushort> RecvIds = [11, 27, 43, 59];

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;
    private const ulong InvalidHandle = ulong.MaxValue;

    private readonly object _gate = new();
    private Dictionary<int, (long Sent, long Received)> _bytes = new();
    private Thread? _thread;
    private ulong _traceHandle = InvalidHandle;
    private IntPtr _loggerName;
    private EventRecordCallback? _callback;   // kept alive while the session runs

    public bool IsRunning { get; private set; }

    public NetworkMonitor()
    {
        // Never leave the ETW session running after Ember exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (IsRunning) StopSessionByName(); };
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    /// <summary>Start the session if possible (admin only). Safe to call repeatedly.</summary>
    public void EnsureStarted()
    {
        if (IsRunning || !Environment.IsPrivilegedProcess) return;
        try
        {
            StopSessionByName();   // a session left behind by a crash would block ours

            var props = NewProperties();
            try
            {
                if (StartTraceW(out var session, SessionName, props) != 0) return;
                var guid = KernelNetwork;
                if (EnableTraceEx2(session, ref guid, 1 /* enable */, 4 /* info */, 0, 0, 0, IntPtr.Zero) != 0)
                {
                    StopSessionByName();
                    return;
                }
            }
            finally { Marshal.FreeHGlobal(props); }

            _callback = OnEvent;
            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            var logFile = Marshal.AllocHGlobal(LogFileSize);
            try
            {
                for (int i = 0; i < LogFileSize; i += 8) Marshal.WriteInt64(logFile, i, 0);
                Marshal.WriteIntPtr(logFile, 8, _loggerName);                                     // LoggerName
                Marshal.WriteInt32(logFile, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
                Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback
                _traceHandle = OpenTraceW(logFile);
            }
            finally { Marshal.FreeHGlobal(logFile); }

            if (_traceHandle == InvalidHandle)
            {
                StopSessionByName();
                return;
            }

            IsRunning = true;
            var handle = _traceHandle;
            _thread = new Thread(() =>
            {
                try { ProcessTrace([handle], 1, IntPtr.Zero, IntPtr.Zero); } catch { }
            })
            { IsBackground = true, Name = "Ember network monitor" };
            _thread.Start();
        }
        catch
        {
            Stop();
        }
    }

    /// <summary>Bytes sent/received per PID since the last call.</summary>
    public Dictionary<int, (long Sent, long Received)> Drain()
    {
        lock (_gate)
        {
            var result = _bytes;
            _bytes = new Dictionary<int, (long, long)>();
            return result;
        }
    }

    private void OnEvent(IntPtr record)
    {
        try
        {
            // EVENT_RECORD: ProviderId @24, EventDescriptor.Id @40, UserDataLength @86, UserData @96.
            var id = (ushort)Marshal.ReadInt16(record, 40);
            var send = SendIds.Contains(id);
            if (!send && !RecvIds.Contains(id)) return;
            if ((ushort)Marshal.ReadInt16(record, 86) < 8) return;
            if (Marshal.PtrToStructure<Guid>(record + 24) != KernelNetwork) return;

            var data = Marshal.ReadIntPtr(record, 96);
            if (data == IntPtr.Zero) return;
            var pid = Marshal.ReadInt32(data, 0);
            var size = (uint)Marshal.ReadInt32(data, 4);

            lock (_gate)
            {
                var current = _bytes.GetValueOrDefault(pid);
                _bytes[pid] = send ? (current.Sent + size, current.Received) : (current.Sent, current.Received + size);
            }
        }
        catch { }
    }

    public void Stop()
    {
        if (!IsRunning && _traceHandle == InvalidHandle) return;
        IsRunning = false;
        StopSessionByName();   // makes ProcessTrace return
        if (_traceHandle != InvalidHandle)
        {
            try { CloseTrace(_traceHandle); } catch { }
            _traceHandle = InvalidHandle;
        }
        _thread?.Join(2000);
        _thread = null;
        if (_loggerName != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_loggerName);
            _loggerName = IntPtr.Zero;
        }
        lock (_gate) _bytes = new Dictionary<int, (long, long)>();
    }

    private static void StopSessionByName()
    {
        var props = NewProperties();
        try { ControlTraceW(0, SessionName, props, ControlStop); } catch { }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes) followed by room for the session name.</summary>
    private static IntPtr NewProperties()
    {
        const int total = PropertiesSize + 1024;
        var p = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i += 8) Marshal.WriteInt64(p, i, 0);
        Marshal.WriteInt32(p, 0, total);                                  // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                                     // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));   // Wnode.Flags
        Marshal.WriteInt32(p, 64, unchecked((int)EventTraceRealTimeMode)); // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                     // FlushTimer (seconds)
        Marshal.WriteInt32(p, 116, PropertiesSize);                       // LoggerNameOffset
        return p;
    }

    public void Dispose() => Stop();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
