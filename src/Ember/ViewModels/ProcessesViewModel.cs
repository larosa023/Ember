using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;

namespace Ember.ViewModels;

public partial class ProcessesViewModel : ViewModelBase
{
    // Ending these can crash or lock up Windows, so Ember refuses.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "Memory Compression", "Secure System", "fontdrvhost", "dwm",
    };

    private readonly Dictionary<int, ProcessRow> _rows = new();
    private IReadOnlyList<ProcessInfo> _last = Array.Empty<ProcessInfo>();
    private bool _descending = true;

    public ObservableCollection<ProcessRow> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sortKey = "cpu";
    [ObservableProperty] private string _summary = "Loading…";
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndTaskCommand), nameof(EndTreeCommand), nameof(OpenLocationCommand))]
    private ProcessRow? _selected;

    public string NameHeader => Header("name", "Name");
    public string PidHeader => Header("pid", "PID");
    public string CpuHeader => Header("cpu", "CPU");
    public string MemoryHeader => Header("mem", "Memory");

    private string Header(string key, string label) =>
        SortKey == key ? $"{label} {(_descending ? "↓" : "↑")}" : label;

    partial void OnSearchChanged(string value) => Update(_last);

    [RelayCommand]
    private void Sort(string key)
    {
        if (SortKey == key) _descending = !_descending;
        else
        {
            SortKey = key;
            _descending = key is "cpu" or "mem";
        }

        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(PidHeader));
        OnPropertyChanged(nameof(CpuHeader));
        OnPropertyChanged(nameof(MemoryHeader));
        Update(_last);
    }

    public void Update(IReadOnlyList<ProcessInfo> list)
    {
        _last = list;
        var query = Search.Trim();
        var seen = new HashSet<int>();
        double totalCpu = 0;
        long totalMemory = 0;

        foreach (var p in list)
        {
            totalCpu += p.Cpu;
            totalMemory += p.Memory;

            if (query.Length > 0 &&
                !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !p.Pid.ToString().Contains(query, StringComparison.Ordinal))
                continue;

            seen.Add(p.Pid);
            if (_rows.TryGetValue(p.Pid, out var row)) row.Apply(p);
            else _rows[p.Pid] = new ProcessRow(p);
        }

        foreach (var pid in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(pid);

        Sync(Ordered());

        Summary = $"{list.Count} processes · {Math.Min(totalCpu, 100):0}% CPU · {Format.Bytes(totalMemory)} in use";
        if (Selected is not null && !_rows.ContainsKey(Selected.Pid)) Selected = null;
    }

    private List<ProcessRow> Ordered()
    {
        Comparison<ProcessRow> compare = SortKey switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "pid" => (a, b) => a.Pid.CompareTo(b.Pid),
            "mem" => (a, b) => a.Memory.CompareTo(b.Memory),
            _ => (a, b) => a.Cpu.CompareTo(b.Cpu),
        };

        var items = _rows.Values.ToList();
        items.Sort((a, b) =>
        {
            var c = compare(a, b);
            if (_descending) c = -c;
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        });
        return items;
    }

    /// <summary>Reorders the visible list in place (Move, not Reset) so selection and scroll survive.</summary>
    private void Sync(List<ProcessRow> desired)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i])) continue;
            var j = Rows.IndexOf(desired[i]);
            if (j >= 0) Rows.Move(j, i);
            else Rows.Insert(i, desired[i]);
        }
        while (Rows.Count > desired.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EndTask() => Kill(tree: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EndTree() => Kill(tree: true);

    private void Kill(bool tree)
    {
        if (Selected is not { } row) return;
        if (Protected.Contains(row.Name))
        {
            Status = $"{row.Name} is a core Windows process. Ending it would crash your PC, so Ember won't.";
            return;
        }

        try
        {
            using var p = Process.GetProcessById(row.Pid);
            p.Kill(tree);
            Status = $"Ended {row.Name} ({row.Pid}).";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't end {row.Name}: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenLocation()
    {
        if (Selected is not { } row) return;
        try
        {
            var path = row.Path;
            if (string.IsNullOrEmpty(path))
            {
                using var p = Process.GetProcessById(row.Pid);
                path = p.MainModule?.FileName;
            }
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("path unavailable");
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open {row.Name}'s location ({ex.Message}). Try running as admin.";
        }
    }
}

public partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessInfo info)
    {
        Pid = info.Pid;
        Name = info.Name;
        Apply(info);
    }

    public int Pid { get; }
    public string Name { get; }

    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _memory;
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private IImage? _icon;
    [ObservableProperty] private string? _path;

    public void Apply(ProcessInfo info)
    {
        Icon = info.Icon;
        Path = info.Path;
        Cpu = info.Cpu;
        Memory = info.Memory;
        CpuText = info.Cpu < 0.05 ? "0%" : $"{info.Cpu:0.0}%";
        MemoryText = Format.Bytes(info.Memory);
    }
}
