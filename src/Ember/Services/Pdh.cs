using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>Minimal wrapper over Windows Performance Data Helper (the API behind Task Manager / perfmon).</summary>
internal static class Pdh
{
    private const uint FmtDouble = 0x00000200;
    private const uint FmtNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;

    /// <summary>Opens a query with one counter per English path (works on any Windows language).</summary>
    public static bool TryOpen(out IntPtr query, out IntPtr[] counters, params string[] paths)
    {
        counters = new IntPtr[paths.Length];
        query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return false;
            bool any = false;
            for (int i = 0; i < paths.Length; i++)
                if (PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out counters[i]) == 0) any = true;
            if (!any) return false;
            PdhCollectQueryData(query); // rate counters need a baseline sample
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool Collect(IntPtr query) => query != IntPtr.Zero && PdhCollectQueryData(query) == 0;

    /// <summary>All instances of a (wildcard) counter as (instance name, value).</summary>
    public static List<(string Instance, double Value)>? ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;

        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != MoreData || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out var count, buffer);
            if (status != 0) return null;

            var values = new List<(string, double)>((int)count);
            int stride = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * stride);
                if (item.CStatus == 0)
                    values.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void Close(ref IntPtr query)
    {
        if (query == IntPtr.Zero) return;
        try { PdhCloseQuery(query); } catch { }
        query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;  // union member used with PDH_FMT_DOUBLE
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
