using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;

namespace Ember.ViewModels;

public partial class StartupViewModel : ViewModelBase
{
    public ObservableCollection<StartupRow> Items { get; } = new();

    [ObservableProperty] private string _status = "";

    [RelayCommand]
    public async Task Refresh()
    {
        try
        {
            // Reading the registry, startup folders and shortcut targets can take a moment: do it off the UI thread.
            var entries = await Task.Run(StartupService.Load);
            Items.Clear();
            foreach (var entry in entries)
                Items.Add(new StartupRow(entry, Toggle));
            Status = SummaryText();
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read startup apps: {ex.Message}";
        }
    }

    private string SummaryText() => $"{Items.Count} startup apps · {Items.Count(i => i.IsEnabled)} enabled";

    private bool Toggle(StartupEntry entry, bool enabled)
    {
        // After a change message, go back to the counts.
        ClearAfter(() => Status = SummaryText());
        try
        {
            StartupService.SetEnabled(entry, enabled);
            Status = $"{entry.Name} {(enabled ? "will" : "won't")} start with Windows.";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Changing apps that start for all users needs admin. Use \"Restart as admin\".";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Couldn't change {entry.Name}: {ex.Message}";
            return false;
        }
    }
}

public partial class StartupRow : ObservableObject
{
    private readonly Func<StartupEntry, bool, bool> _toggle;
    private bool _suppress = true;

    public StartupRow(StartupEntry entry, Func<StartupEntry, bool, bool> toggle)
    {
        Entry = entry;
        _toggle = toggle;
        IsEnabled = entry.Enabled;
        Icon = LoadIcon(entry.Command);
        _suppress = false;
    }

    public StartupEntry Entry { get; }
    public IImage? Icon { get; } 
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Location => Entry.Location;

    [ObservableProperty] private bool _isEnabled;

    /// <summary>The icon of the exe a startup command launches (generic icon if it can't be read).</summary>
    private static IImage? LoadIcon(string command)
    {
        try
        {
            var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
            string? path;
            if (cmd.StartsWith('"'))
            {
                var end = cmd.IndexOf('"', 1);
                path = end > 1 ? cmd[1..end] : null;
            }
            else
            {
                var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe > 0 ? cmd[..(exe + 4)] : cmd;
            }

            if (path is not null && File.Exists(path) && Services.Native.ExtractIcon(path) is { } icon)
                return icon;
        }
        catch { }

        try { return Services.Native.GenericAppIcon(); } catch { return null; }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        if (_toggle(Entry, value)) return;

        // Revert the switch if Windows refused the change.
        Dispatcher.UIThread.Post(() =>
        {
            _suppress = true;
            IsEnabled = !value;
            _suppress = false;
        });
    }
}
