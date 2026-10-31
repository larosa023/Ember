using System.Diagnostics;
using Avalonia.Threading;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// One background loop that samples sensors (and processes, only while that page is open),
/// then hands results to the UI thread. Keeps the UI thread free so everything stays smooth.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly SensorService _sensors;
    private readonly ProcessService _processes;
    private readonly SystemInfoService _system = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile int _intervalMs = 1000;
    private volatile bool _sampleProcesses;

    public MonitorHost(SensorService sensors, ProcessService processes)
    {
        _sensors = sensors;
        _processes = processes;
    }

    public event Action<HardwareSnapshot, IReadOnlyList<ProcessInfo>?>? Tick;

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 250, 10_000);
    }

    public string? PreferredGpu
    {
        get => _sensors.PreferredGpu;
        set => _sensors.PreferredGpu = value;
    }

    /// <summary>Whether per-process network numbers are being collected (admin only).</summary>
    public bool ProcessNetworkAvailable => _processes.NetworkAvailable;

    public bool SampleProcesses
    {
        get => _sampleProcesses;
        set => _sampleProcesses = value;
    }

    private Task? _loop;

    public FanController Fans => _sensors.Fans;

    /// <summary>Set before Start(); samples are recorded on the monitor thread.</summary>
    public HistoryService? History { get; set; }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();

            HardwareSnapshot snapshot;
            try { snapshot = _sensors.Read(); }
            catch (Exception ex)
            {
                Trace.WriteLine($"Sensor read failed: {ex}");
                snapshot = HardwareSnapshot.Empty;
            }

            try { snapshot = _system.Enrich(snapshot); }
            catch (Exception ex) { Trace.WriteLine($"System info failed: {ex}"); }

            try { History?.Record(snapshot); }
            catch (Exception ex) { Trace.WriteLine($"History failed: {ex}"); }

            IReadOnlyList<ProcessInfo>? processes = null;
            if (_sampleProcesses)
            {
                try { processes = _processes.Sample(); }
                catch (Exception ex) { Trace.WriteLine($"Process sample failed: {ex}"); }
            }
            else
            {
                _processes.Reset();
            }

            Dispatcher.UIThread.Post(() => Tick?.Invoke(snapshot, processes));

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            try { await Task.Delay(Math.Max(50, _intervalMs - elapsed), token); }
            catch (OperationCanceledException) { break; }
        }

        _sensors.Dispose();
        _processes.Dispose();
    }

    /// <summary>Stops the loop and waits briefly so fans are restored to BIOS control before exit.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
