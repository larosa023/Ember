using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;

namespace Ember.ViewModels;

public enum ProcessCategory { Apps, Background, Windows }

/// <summary>
/// Task Manager-style process list: sections (Apps / Background / Windows), each holding
/// groups of processes that share an exe (e.g. all of Chrome), expandable to individual PIDs.
/// The visible list is one flat collection that is reordered in place every tick.
/// </summary>
public partial class ProcessesViewModel : ViewModelBase
{
    // Ending these can crash or lock up Windows, so Ember refuses.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "Memory Compression", "Secure System", "fontdrvhost", "dwm",
    };

    private readonly Dictionary<string, ProcessGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessRow> _children = new();
    private readonly SectionRow[] _sections;
    private IReadOnlyList<ProcessInfo> _last = Array.Empty<ProcessInfo>();
    private bool _descending = true;

    public ProcessesViewModel()
    {
        _sections =
        [
            new SectionRow(ProcessCategory.Apps, "Apps"),
            new SectionRow(ProcessCategory.Background, "Background processes"),
            new SectionRow(ProcessCategory.Windows, "Windows processes") { IsCollapsed = true },
        ];
    }

    /// <summary>Mixed rows: SectionRow, ProcessGroup, ProcessRow (expanded children).</summary>
    public ObservableCollection<object> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sortKey = "cpu";
    [ObservableProperty] private string _summary = "Loading…";
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndTaskCommand), nameof(EndTreeCommand), nameof(OpenLocationCommand))]
    private object? _selected;

    public string NameHeader => Header("name", "Name");
    public string PidHeader => Header("pid", "PID");
    public string CpuHeader => Header("cpu", "CPU");
    public string GpuHeader => Header("gpu", "GPU");
    public string MemoryHeader => Header("mem", "Memory");

    private string Header(string key, string label) =>
        SortKey == key ? $"{label} {(_descending ? "↓" : "↑")}" : label;

    partial void OnSearchChanged(string value) => Refresh();

    partial void OnSelectedChanged(object? value)
    {
        // Clicking a section header collapses/expands it instead of selecting it.
        if (value is SectionRow section)
        {
            section.IsCollapsed = !section.IsCollapsed;
            Dispatcher.UIThread.Post(() =>
            {
                Selected = null;
                Refresh();
            });
        }
    }

    [RelayCommand]
    private void Sort(string key)
    {
        if (SortKey == key) _descending = !_descending;
        else
        {
            SortKey = key;
            _descending = key is "cpu" or "mem" or "gpu";
        }

        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(PidHeader));
        OnPropertyChanged(nameof(CpuHeader));
        OnPropertyChanged(nameof(GpuHeader));
        OnPropertyChanged(nameof(MemoryHeader));
        Refresh();
    }

    public void Refresh() => Update(_last);

    public void Update(IReadOnlyList<ProcessInfo> list)
    {
        _last = list;
        var query = Search.Trim();

        // 1. Bucket processes by exe (or name when the path is hidden).
        var buckets = new Dictionary<string, List<ProcessInfo>>(StringComparer.OrdinalIgnoreCase);
        double totalCpu = 0;
        long totalMemory = 0;
        foreach (var p in list)
        {
            totalCpu += p.Cpu;
            totalMemory += p.Memory;
            if (query.Length > 0 && !Matches(p, query)) continue;

            var key = p.Path ?? "name:" + p.Name;
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<ProcessInfo>();
            bucket.Add(p);
        }

        // 2. Update or create groups and their child rows (reused so selection survives).
        var livePids = new HashSet<int>();
        foreach (var (key, procs) in buckets)
        {
            if (!_groups.TryGetValue(key, out var group))
                _groups[key] = group = new ProcessGroup(key, Refresh);

            var rows = new List<ProcessRow>(procs.Count);
            foreach (var p in procs)
            {
                livePids.Add(p.Pid);
                if (_children.TryGetValue(p.Pid, out var row) && row.Name == p.Name) row.Apply(p);
                else _children[p.Pid] = row = new ProcessRow(p);
                rows.Add(row);
            }
            group.Apply(procs, rows);
        }

        foreach (var key in _groups.Keys.Where(k => !buckets.ContainsKey(k)).ToList()) _groups.Remove(key);
        foreach (var pid in _children.Keys.Where(k => !livePids.Contains(k)).ToList()) _children.Remove(pid);

        // 3. Build the visible list: section header, its groups, expanded children.
        var desired = new List<object>(_children.Count + 8);
        int apps = 0, background = 0, windows = 0;
        foreach (var section in _sections)
        {
            var groups = _groups.Values.Where(g => g.Category == section.Category).ToList();
            groups.Sort(CompareGroups);
            section.Count = groups.Count;
            switch (section.Category)
            {
                case ProcessCategory.Apps: apps = groups.Count; break;
                case ProcessCategory.Background: background = groups.Count; break;
                default: windows = groups.Count; break;
            }
            if (groups.Count == 0) continue;

            desired.Add(section);
            if (section.IsCollapsed && query.Length == 0) continue;

            foreach (var g in groups)
            {
                desired.Add(g);
                if (g.IsExpanded && g.Count > 1)
                {
                    var kids = g.Children.ToList();
                    kids.Sort(CompareRows);
                    desired.AddRange(kids);
                }
            }
        }

        Sync(desired);

        Summary = $"{apps} apps · {background} background · {windows} Windows · " +
                  $"{Math.Min(totalCpu, 100):0}% CPU · {Format.Bytes(totalMemory)} in use";

        if (Selected is not null && Selected is not SectionRow && !desired.Contains(Selected))
            Selected = null;
    }

    private static bool Matches(ProcessInfo p, string q) =>
        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        (p.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        p.Pid.ToString().Contains(q, StringComparison.Ordinal);

    private int CompareGroups(ProcessGroup a, ProcessGroup b)
    {
        int c = SortKey switch
        {
            "name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "pid" => a.FirstPid.CompareTo(b.FirstPid),
            "mem" => a.Memory.CompareTo(b.Memory),
            "gpu" => a.Gpu.CompareTo(b.Gpu),
            _ => a.Cpu.CompareTo(b.Cpu),
        };
        if (_descending) c = -c;
        return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
    }

    private int CompareRows(ProcessRow a, ProcessRow b)
    {
        int c = SortKey switch
        {
            "name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "pid" => a.Pid.CompareTo(b.Pid),
            "mem" => a.Memory.CompareTo(b.Memory),
            "gpu" => a.Gpu.CompareTo(b.Gpu),
            _ => a.Cpu.CompareTo(b.Cpu),
        };
        if (_descending) c = -c;
        return c != 0 ? c : a.Pid.CompareTo(b.Pid);
    }

    /// <summary>Reorders the visible list in place (Move, not Reset) so selection and scroll survive.</summary>
    private void Sync(List<object> desired)
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

    // ---------- Actions ----------

    private bool HasSelection() => Selected is ProcessGroup or ProcessRow;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EndTask() => Kill(tree: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EndTree() => Kill(tree: true);

    private void Kill(bool tree)
    {
        var targets = Selected switch
        {
            ProcessGroup g => g.Children.Select(c => (c.Pid, c.Name)).ToList(),
            ProcessRow r => new List<(int Pid, string Name)> { (r.Pid, r.Name) },
            _ => new List<(int Pid, string Name)>(),
        };
        if (targets.Count == 0) return;

        var label = Selected is ProcessGroup group ? group.Name : targets[0].Name;
        if (targets.Any(t => Protected.Contains(t.Name)))
        {
            Status = $"{label} is a core Windows process. Ending it would crash your PC, so Ember won't.";
            return;
        }

        int ended = 0;
        string? error = null;
        foreach (var (pid, _) in targets)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(tree);
                ended++;
            }
            catch (ArgumentException) { ended++; }  // already gone (e.g. killed with its parent)
            catch (Exception ex) { error = ex.Message; }
        }

        Status = error is null
            ? targets.Count == 1 ? $"Ended {label} ({targets[0].Pid})." : $"Ended {label} ({ended} processes)."
            : $"Couldn't end all of {label}: {error}. Try running as admin.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenLocation()
    {
        string? path;
        string name;
        int pid;
        if (Selected is ProcessGroup g) { path = g.Path; name = g.Name; pid = g.FirstPid; }
        else if (Selected is ProcessRow r) { path = r.Path; name = r.Name; pid = r.Pid; }
        else return;

        try
        {
            if (string.IsNullOrEmpty(path))
            {
                using var p = Process.GetProcessById(pid);
                path = p.MainModule?.FileName;
            }
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("path unavailable");
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open {name}'s location ({ex.Message}). Try running as admin.";
        }
    }
}

/// <summary>Section header row ("Apps (5)"). Clicking it collapses or expands the section.</summary>
public partial class SectionRow : ObservableObject
{
    public SectionRow(ProcessCategory category, string title)
    {
        Category = category;
        Title = title;
    }

    public ProcessCategory Category { get; }
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isCollapsed;

    public string Header => $"{Title} ({Count})";
    public string Chevron => IsCollapsed ? "" : "";   // right / down
}

/// <summary>All processes running the same exe, shown as one row (like Task Manager).</summary>
public partial class ProcessGroup : ObservableObject
{
    private readonly Action _refresh;

    public ProcessGroup(string key, Action refresh)
    {
        Key = key;
        _refresh = refresh;
    }

    public string Key { get; }
    public List<ProcessRow> Children { get; private set; } = new();
    public ProcessCategory Category { get; private set; }
    public string? Path { get; private set; }
    public int FirstPid { get; private set; }
    public double Cpu { get; private set; }
    public double Gpu { get; private set; }
    public long Memory { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IImage? _icon;
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _pidText = "";
    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private string _countText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChildren))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public bool HasChildren => Count > 1;
    public string Chevron => IsExpanded ? "" : "";

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _refresh();
    }

    public void Apply(List<ProcessInfo> procs, List<ProcessRow> rows)
    {
        var first = procs[0];
        Children = rows;
        Path = first.Path;
        FirstPid = procs.Min(p => p.Pid);

        Category = procs.Any(p => p.HasWindow) ? ProcessCategory.Apps
            : first.IsWindows ? ProcessCategory.Windows
            : ProcessCategory.Background;

        Name = first.Description ?? first.Name;
        Icon = first.Icon;
        Count = procs.Count;
        CountText = procs.Count > 1 ? $"({procs.Count})" : "";
        PidText = procs.Count == 1 ? first.Pid.ToString() : "";

        Cpu = Math.Min(procs.Sum(p => p.Cpu), 100);
        Memory = procs.Sum(p => p.Memory);
        CpuText = Cpu < 0.05 ? "0%" : $"{Cpu:0.0}%";
        Gpu = Math.Min(procs.Sum(p => p.Gpu), 100);
        GpuText = Gpu < 0.05 ? "0%" : $"{Gpu:0.0}%";
        MemoryText = Format.Bytes(Memory);
    }
}

/// <summary>One process (shown when its group is expanded).</summary>
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
    [ObservableProperty] private double _gpu;
    [ObservableProperty] private string _gpuText = "";
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
        Gpu = info.Gpu;
        GpuText = info.Gpu < 0.05 ? "0%" : $"{info.Gpu:0.0}%";
        MemoryText = Format.Bytes(info.Memory);
    }
}
