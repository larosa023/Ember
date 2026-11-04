using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
        try { _show = CreateOpenEvent() ?? new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
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

    /// <summary>
    /// The "show yourself" event, created so that a normal (non-admin) launch can still signal it when
    /// the running Ember is elevated: Everyone may modify it, and the low integrity label allows writes
    /// from lower-integrity processes.
    /// </summary>
    private static EventWaitHandle? CreateOpenEvent()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x1F0003;;;WD)S:(ML;;NW;;;LW)", 1, out var sd, IntPtr.Zero))
            return null;
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = sd };
            var handle = CreateEventW(ref sa, false, false, ShowEventName);
            if (handle.IsInvalid) return null;
            return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
        }
        finally
        {
            LocalFree(sd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
