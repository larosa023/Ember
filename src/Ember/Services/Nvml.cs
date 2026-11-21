using System.Runtime.InteropServices;
using System.Text;

namespace Ember.Services;

/// <summary>
/// NVIDIA's management library (nvml.dll, installed with every GeForce driver). Used for the GPU
/// power limit, which is the safe, officially supported tuning knob: the driver enforces the card's own
/// minimum and maximum, and the limit resets on reboot or driver restart.
/// Everything here fails quietly on PCs without an NVIDIA GPU.
/// </summary>
public static class Nvml
{
    private const int Success = 0;
    private const int NoPermission = 4;

    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _ready;

    public sealed record PowerInfo(IntPtr Handle, string Name, double MinW, double MaxW, double DefaultW, double CurrentW);

    /// <summary>Loads NVML once. False when there's no NVIDIA driver.</summary>
    public static bool EnsureReady()
    {
        lock (Gate)
        {
            if (_tried) return _ready;
            _tried = true;
            try { _ready = nvmlInit_v2() == Success; }
            catch (DllNotFoundException) { _ready = false; }
            catch (EntryPointNotFoundException) { _ready = false; }
            catch (BadImageFormatException) { _ready = false; }
            return _ready;
        }
    }

    /// <summary>Every NVIDIA GPU that reports an adjustable power limit.</summary>
    public static List<PowerInfo> ReadPower()
    {
        var list = new List<PowerInfo>();
        if (!EnsureReady()) return list;
        lock (Gate)
        {
            if (nvmlDeviceGetCount_v2(out var count) != Success) return list;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var dev) != Success) continue;
                if (nvmlDeviceGetPowerManagementLimitConstraints(dev, out var min, out var max) != Success) continue;
                if (nvmlDeviceGetPowerManagementDefaultLimit(dev, out var def) != Success) continue;
                if (nvmlDeviceGetPowerManagementLimit(dev, out var cur) != Success) cur = def;
                if (max <= min) continue;   // not adjustable (most laptops)
                list.Add(new PowerInfo(dev, Name(dev), min / 1000.0, max / 1000.0, def / 1000.0, cur / 1000.0));
            }
        }
        return list;
    }

    /// <summary>Sets the power limit in watts. Returns null on success, otherwise a message.</summary>
    public static string? SetPowerLimit(IntPtr device, double watts)
    {
        if (!EnsureReady()) return "NVIDIA driver not found.";
        lock (Gate)
        {
            var rc = nvmlDeviceSetPowerManagementLimit(device, (uint)Math.Round(watts * 1000));
            return rc switch
            {
                Success => null,
                NoPermission => "Changing the power limit needs admin. Use \"Restart as admin\".",
                _ => $"The driver refused the change (NVML error {rc}).",
            };
        }
    }

    private static string Name(IntPtr device)
    {
        var buffer = new byte[96];
        return nvmlDeviceGetName(device, buffer, (uint)buffer.Length) == Success
            ? Encoding.UTF8.GetString(buffer).TrimEnd('\0').Trim()
            : "NVIDIA GPU";
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint mw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint mw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint mw);
}
