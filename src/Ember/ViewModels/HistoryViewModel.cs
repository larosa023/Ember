using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Controls;
using Ember.Services;

namespace Ember.ViewModels;

public sealed record RangeOption(TimeSpan Span, string Label)
{
    public override string ToString() => Label;
}

/// <summary>History page: temperatures, usage, power and network over the chosen time range.</summary>
public partial class HistoryViewModel : ViewModelBase
{
    private const int MaxPoints = 300;
    private readonly HistoryService _history;
    private DateTime _lastBuilt = DateTime.MinValue;
    private int _lastCount = -1;

    public HistoryViewModel(HistoryService history)
    {
        _history = history;
        _selectedRange = Ranges[1];
        Charts = [Temps, Usage, Power, Network];
    }

    public IReadOnlyList<RangeOption> Ranges { get; } =
    [
        new(TimeSpan.FromMinutes(10), "Last 10 minutes"),
        new(TimeSpan.FromHours(1), "Last hour"),
        new(TimeSpan.FromHours(6), "Last 6 hours"),
        new(TimeSpan.FromHours(24), "Last 24 hours"),
    ];

    public ChartCard Temps { get; } = new("Temperatures");
    public ChartCard Usage { get; } = new("Usage");
    public ChartCard Power { get; } = new("Power draw");
    public ChartCard Network { get; } = new("Network");
    public IReadOnlyList<ChartCard> Charts { get; }

    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _status = "";

    partial void OnSelectedRangeChanged(RangeOption value) => Refresh();

    /// <summary>Rebuild when new samples arrived (called every tick while the page is open).</summary>
    public void RefreshIfChanged()
    {
        if (_history.Count != _lastCount || DateTime.Now - _lastBuilt > HistoryService.Interval)
            Refresh();
    }

    public void Refresh()
    {
        var now = DateTime.Now;
        var span = SelectedRange.Span;
        var start = now - span;
        var samples = _history.Snapshot(start);
        _lastCount = _history.Count;
        _lastBuilt = now;

        IsEmpty = samples.Count < 2;
        Summary = IsEmpty
            ? "Collecting data. The first points appear within a few seconds."
            : $"{samples.Count:N0} readings, one every 5 seconds";
        if (IsEmpty) return;

        // Bucket into at most MaxPoints points (one per 5 s for short ranges).
        var buckets = (int)Math.Clamp(span / HistoryService.Interval, 2, MaxPoints);
        var bucketSpan = span / buckets;
        var times = new DateTime[buckets];
        for (int i = 0; i < buckets; i++) times[i] = start + bucketSpan * (i + 0.5);

        double[] Series(Func<HistorySample, float> pick, Func<double, double>? convert = null)
        {
            var sum = new double[buckets];
            var count = new int[buckets];
            foreach (var x in samples)
            {
                var v = pick(x);
                if (float.IsNaN(v)) continue;
                var b = (int)Math.Clamp((x.Time - start) / bucketSpan, 0, buckets - 1);
                sum[b] += convert is null ? v : convert(v);
                count[b]++;
            }
            var result = new double[buckets];
            for (int i = 0; i < buckets; i++) result[i] = count[i] > 0 ? sum[i] / count[i] : double.NaN;
            return result;
        }

        Func<double, double>? toUnit = Format.UseFahrenheit ? (double c) => c * 9 / 5 + 32 : null;
        static double Mbps(double bytesPerSecond) => bytesPerSecond * 8 / 1e6;

        var cpuT = Series(x => x.CpuTemp, toUnit);
        var gpuT = Series(x => x.GpuTemp, toUnit);
        Temps.Set(times, [new("CPU", cpuT), new("GPU", gpuT)], AutoMin(cpuT, gpuT), AutoMax(10, cpuT, gpuT),
            v => $"{v:0}°", (avg, max) => $"avg {avg:0}° · max {max:0}°");

        Usage.Set(times,
            [new("CPU", Series(x => x.CpuLoad)), new("GPU", Series(x => x.GpuLoad)), new("RAM", Series(x => x.RamLoad))],
            0, 100, v => $"{v:0}%", (avg, max) => $"avg {avg:0}% · max {max:0}%");

        var cpuW = Series(x => x.CpuPower);
        var gpuW = Series(x => x.GpuPower);
        Power.Set(times, [new("CPU", cpuW), new("GPU", gpuW)], 0, Math.Max(50, AutoMax(25, cpuW, gpuW)),
            v => $"{v:0} W", (avg, max) => $"avg {avg:0} W · max {max:0} W");

        var down = Series(x => x.NetDown, Mbps);
        var up = Series(x => x.NetUp, Mbps);
        Network.Set(times, [new("Down", down), new("Up", up)], 0, Math.Max(1, NiceMax(down, up)),
            v => v < 10 ? $"{v:0.#} Mb" : $"{v:0} Mb", (avg, max) => $"avg {avg:0.#} · peak {max:0.#} Mbps");
    }

    private static IEnumerable<double> Valid(params double[][] series) => series.SelectMany(s => s).Where(v => !double.IsNaN(v));

    private static double AutoMin(params double[][] series)
    {
        var values = Valid(series).ToList();
        return values.Count == 0 ? 0 : Math.Max(0, Math.Floor((values.Min() - 5) / 10) * 10);
    }

    private static double AutoMax(double step, params double[][] series)
    {
        var values = Valid(series).ToList();
        return values.Count == 0 ? 100 : Math.Ceiling((values.Max() + step / 2) / step) * step;
    }

    private static double NiceMax(params double[][] series)
    {
        var values = Valid(series).ToList();
        if (values.Count == 0) return 1;
        var max = values.Max() * 1.15;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(max, 0.001))));
        return Math.Ceiling(max / magnitude) * magnitude;
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(HistoryService.LogsDir);
            Process.Start(new ProcessStartInfo(HistoryService.LogsDir) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task Export()
    {
        var top = App.MainWindowInstance;
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export history",
            SuggestedFileName = $"ember-history-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV spreadsheet") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;

        try
        {
            var samples = _history.Snapshot(DateTime.Now - SelectedRange.Span);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(HistoryService.CsvHeader);
            foreach (var x in samples) await writer.WriteLineAsync(HistoryService.ToCsvLine(x));
            Status = $"Exported {samples.Count:N0} readings to {file.Name}.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't export: {ex.Message}";
        }
    }
}

public partial class ChartCard : ObservableObject
{
    public ChartCard(string title) => Title = title;

    public string Title { get; }
    public ObservableCollection<LegendItem> Legend { get; } = new();

    [ObservableProperty] private ChartData? _data;
    [ObservableProperty] private bool _hasData;

    public void Set(DateTime[] times, List<ChartLine> lines, double min, double max,
                    Func<double, string> label, Func<double, double, string> stats)
    {
        // Drop lines with no data at all (e.g. no GPU power sensor).
        lines = lines.Where(l => l.Values.Any(v => !double.IsNaN(v))).ToList();
        HasData = lines.Count > 0;
        if (max <= min) max = min + 1;
        Data = new ChartData(times, lines, min, max, label);

        while (Legend.Count > lines.Count) Legend.RemoveAt(Legend.Count - 1);
        for (int i = 0; i < lines.Count; i++)
        {
            var valid = lines[i].Values.Where(v => !double.IsNaN(v)).ToList();
            var text = valid.Count == 0 ? "" : stats(valid.Average(), valid.Max());
            if (i < Legend.Count && Legend[i].Name == lines[i].Name) Legend[i].Stats = text;
            else if (i < Legend.Count) Legend[i] = new LegendItem(i, lines[i].Name) { Stats = text };
            else Legend.Add(new LegendItem(i, lines[i].Name) { Stats = text });
        }
    }
}

public partial class LegendItem : ObservableObject
{
    public LegendItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public int Index { get; }
    public string Name { get; }
    public bool IsSecond => Index == 1;
    public bool IsThird => Index == 2;

    [ObservableProperty] private string _stats = "";
}
