namespace Ember.Services;

/// <summary>Keeps one Ember running; a second launch signals the first to show its window.</summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\Ember.SingleInstance";
    private const string ShowEventName = @"Local\Ember.Show";

    private static Mutex? _mutex;          // held for the app's lifetime
    private static EventWaitHandle? _show;

    public static bool TryAcquire(bool waitForPrevious)
    {
        try
        {
            _mutex = new Mutex(false, MutexName);
            bool acquired;
            try { acquired = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }  // previous instance crashed

            if (acquired) return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated Ember owns the mutex; fall through and ask it to show itself.
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch { /* best effort */ }

        return false;
    }

    public static void ListenForShow(Action onShow)
    {
        try { _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch { return; }

        var thread = new Thread(() =>
        {
            while (true)
            {
                _show.WaitOne();
                onShow();
            }
        })
        { IsBackground = true, Name = "Ember single-instance listener" };
        thread.Start();
    }
}
