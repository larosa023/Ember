namespace Ember.Services;

/// <summary>
/// Keeps the saved NVIDIA power limit applied while Ember runs and puts the card back to its
/// default when Ember exits (same safety rule as fans).
/// </summary>
public static class GpuPower
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, double> Changed = new();   // device → default watts
    private static bool _hooked;

    /// <summary>Called once at launch (off the UI thread): re-applies the user's saved limit if Ember is admin.</summary>
    public static void ApplySaved(SettingsService settings)
    {
        if (settings.Current.GpuPowerLimitW is not double watts || !Environment.IsPrivilegedProcess) return;
        try
        {
            var gpu = Nvml.ReadPower().FirstOrDefault(g => settings.Current.GpuPowerLimitGpu is null || g.Name == settings.Current.GpuPowerLimitGpu);
            if (gpu is not null) Set(gpu, watts);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>Clamped to the card's own range. Returns null on success, otherwise a message.</summary>
    public static string? Set(Nvml.PowerInfo gpu, double watts)
    {
        watts = Math.Clamp(watts, gpu.MinW, gpu.MaxW);
        var error = Nvml.SetPowerLimit(gpu.Handle, watts);
        if (error is not null) return error;
        lock (Gate)
        {
            if (Math.Abs(watts - gpu.DefaultW) < 0.5) Changed.Remove(gpu.Handle);
            else Changed.TryAdd(gpu.Handle, gpu.DefaultW);
            if (!_hooked)
            {
                _hooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAll();
            }
        }
        return null;
    }

    public static void RestoreAll()
    {
        lock (Gate)
        {
            foreach (var (device, watts) in Changed)
            {
                try { Nvml.SetPowerLimit(device, watts); } catch { }
            }
            Changed.Clear();
        }
    }
}
