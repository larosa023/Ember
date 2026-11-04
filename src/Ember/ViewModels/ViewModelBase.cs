using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ember.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private DispatcherTimer? _clearTimer;
    private Action? _clear;

    /// <summary>Runs <paramref name="clear"/> once after a pause (restarted by each new call) — for "Copied." style messages.</summary>
    protected void ClearAfter(Action clear, double seconds = 8)
    {
        _clear = clear;
        _clearTimer ??= new DispatcherTimer();
        _clearTimer.Stop();
        _clearTimer.Interval = TimeSpan.FromSeconds(seconds);
        _clearTimer.Tick -= OnClearTick;
        _clearTimer.Tick += OnClearTick;
        _clearTimer.Start();
    }

    private void OnClearTick(object? sender, EventArgs e)
    {
        _clearTimer?.Stop();
        _clear?.Invoke();
        _clear = null;
    }
}
