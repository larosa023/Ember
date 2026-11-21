using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>Windows power plans (Balanced, High performance, ...): list them and switch the active one. No admin needed.</summary>
public static class PowerPlans
{
    public sealed record Plan(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    private const uint AccessScheme = 16;   // ACCESS_SCHEME

    public static List<Plan> List()
    {
        var plans = new List<Plan>();
        try
        {
            var buffer = new byte[16];
            for (uint i = 0; ; i++)
            {
                uint size = 16;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size) != 0) break;
                var id = new Guid(buffer);
                plans.Add(new Plan(id, FriendlyName(id)));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return plans;
    }

    public static Guid? Active()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }
        catch { return null; }
    }

    public static bool SetActive(Guid id)
    {
        try { return PowerSetActiveScheme(IntPtr.Zero, ref id) == 0; }
        catch { return false; }
    }

    private static string FriendlyName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return "Power plan";
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "Power plan";
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootKey, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags,
        uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup,
        IntPtr powerSetting, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
