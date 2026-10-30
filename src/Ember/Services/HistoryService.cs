using System.Globalization;
using System.Text;
using Ember.Models;

namespace Ember.Services;

/// <summary>One reading every 5 seconds. NaN = not available.</summary>
public readonly record struct HistorySample(
    DateTime Time,
    float CpuLoad, float CpuTemp,
    float GpuLoad, float GpuTemp,
    float RamLoad,
    float CpuPower, float GpuPower,
    float NetDown, float NetUp);

/// <summary>
/// Keeps the last 24 hours in memory for the History page and (optionally) appends every sample to
/// %AppData%\Ember\logs\ember-YYYY-MM-DD.csv, deleting logs older than the retention setting.
/// Record() runs on the monitor thread; Snapshot() is safe from the UI thread.
/// </summary>
public sealed class HistoryService : IDisposable
{
    public static readonly string LogsDir = Path.Combine(SettingsService.Dir, "logs");
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    private const string Header = "time,cpu_load_pct,cpu_temp_c,gpu_load_pct,gpu_temp_c,ram_load_pct,cpu_power_w,gpu_power_w,net_down_mbps,net_up_mbps";

    private readonly SettingsService _settings;
    private readonly List<HistorySample> _samples = new();
    private readonly object _gate = new();
    private DateTime _last = DateTime.MinValue;
    private StreamWriter? _writer;
    private DateOnly _writerDay;

    public HistoryService(SettingsService settings)
    {
        _settings = settings;
        try
        {
            CleanUpOldLogs();
            LoadRecentLogs();
        }
        catch
        {
            // History is a nice-to-have; never block startup on it.
        }
    }

    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    public void Record(HardwareSnapshot s)
    {
        var now = DateTime.Now;
        if (now - _last < Interval) return;
        _last = now;

        var primary = s.Network.FirstOrDefault(n => n.IsPrimary);
        var sample = new HistorySample(now,
            V(s.CpuLoad), V(s.CpuTemp), V(s.GpuLoad), V(s.GpuTemp), V(s.RamLoad),
            V(s.CpuPower), V(s.GpuPower),
            primary is null ? float.NaN : (float)primary.DownBps,
            primary is null ? float.NaN : (float)primary.UpBps);

        lock (_gate)
        {
            _samples.Add(sample);
            var cutoff = now - Keep;
            var drop = 0;
            while (drop < _samples.Count && _samples[drop].Time < cutoff) drop++;
            if (drop > 0) _samples.RemoveRange(0, drop);
        }

        if (_settings.Current.HistoryToDisk)
        {
            try { Append(sample); } catch { /* disk full / locked: keep going in memory */ }
        }
        else
        {
            CloseWriter();
        }
    }

    /// <summary>Copy of the samples newer than <paramref name="since"/>.</summary>
    public List<HistorySample> Snapshot(DateTime since)
    {
        lock (_gate)
        {
            var start = _samples.FindIndex(x => x.Time >= since);
            return start < 0 ? new List<HistorySample>() : _samples.GetRange(start, _samples.Count - start);
        }
    }

    public static string ToCsvLine(HistorySample x)
    {
        static string F(float v, string fmt = "0.#") => float.IsNaN(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);
        return string.Join(',',
            x.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            F(x.CpuLoad), F(x.CpuTemp), F(x.GpuLoad), F(x.GpuTemp), F(x.RamLoad),
            F(x.CpuPower), F(x.GpuPower),
            F(x.NetDown * 8 / 1e6f, "0.###"), F(x.NetUp * 8 / 1e6f, "0.###"));
    }

    public static string CsvHeader => Header;

    private void Append(HistorySample sample)
    {
        var day = DateOnly.FromDateTime(sample.Time);
        if (_writer is null || day != _writerDay)
        {
            CloseWriter();
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, $"ember-{day:yyyy-MM-dd}.csv");
            var isNew = !File.Exists(path);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true,
            };
            _writerDay = day;
            if (isNew) _writer.WriteLine(Header);
            CleanUpOldLogs();
        }
        _writer.WriteLine(ToCsvLine(sample));
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    private void CleanUpOldLogs()
    {
        if (!Directory.Exists(LogsDir)) return;
        var keepDays = Math.Max(1, _settings.Current.HistoryDays);
        var oldest = DateOnly.FromDateTime(DateTime.Now).AddDays(-keepDays + 1);
        foreach (var file in Directory.EnumerateFiles(LogsDir, "ember-*.csv"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["ember-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>Reload yesterday's and today's logs so the History page survives a restart.</summary>
    private void LoadRecentLogs()
    {
        if (!Directory.Exists(LogsDir)) return;
        var cutoff = DateTime.Now - Keep;
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var day in new[] { today.AddDays(-1), today })
        {
            var path = Path.Combine(LogsDir, $"ember-{day:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) continue;

            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var p = line.Split(',');
                if (p.Length < 10 || !DateTime.TryParseExact(p[0], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    continue;   // header or damaged line
                if (t < cutoff) continue;

                static float P(string v) => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN;
                _samples.Add(new HistorySample(t, P(p[1]), P(p[2]), P(p[3]), P(p[4]), P(p[5]), P(p[6]), P(p[7]),
                    P(p[8]) * 1e6f / 8, P(p[9]) * 1e6f / 8));
            }
        }
        _samples.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static float V(float? v) => v ?? float.NaN;

    public void Dispose() => CloseWriter();
}
