using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace Ember.Services;

/// <summary>A parsed shortcut such as "Ctrl+Alt+E".</summary>
public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["PrintScreen"] = 0x2C,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
        [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
        ["Num*"] = 0x6A, ["Num+"] = 0x6B, ["Num-"] = 0x6D, ["Num/"] = 0x6F,
    };

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        string? keyName = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= Ctrl; break;
                case "alt": mods |= Alt; break;
                case "shift": mods |= Shift; break;
                case "win": mods |= Win; break;
                case "": keyName = "+"; break;
                default: keyName = part; break;
            }
        }
        var vk = keyName is null ? 0 : KeyToVk(keyName);
        return vk == 0 ? null : new Hotkey(mods, vk, Format(mods, keyName!));
    }

    private static uint KeyToVk(string name)
    {
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
            return (uint)(0x60 + (name[3] - '0'));
        return Named.TryGetValue(name, out var vk) ? vk : 0;
    }

    public static string Format(uint mods, string key)
    {
        var parts = new List<string>();
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if ((mods & Shift) != 0) parts.Add("Shift");
        if ((mods & Win) != 0) parts.Add("Win");
        parts.Add(key);
        return string.Join('+', parts);
    }

    /// <summary>Turns a key press from the Settings capture box into shortcut text (null = not a usable key).</summary>
    public static string? FromKeyEvent(Key key, KeyModifiers modifiers)
    {
        string? name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Num{key - Key.NumPad0}",
            Key.Space => "Space", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Home => "Home", Key.End => "End", Key.Insert => "Insert", Key.Delete => "Delete",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            Key.Pause => "Pause", Key.Scroll => "ScrollLock", Key.PrintScreen => "PrintScreen",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            Key.Multiply => "Num*", Key.Add => "Num+", Key.Subtract => "Num-", Key.Divide => "Num/",
            _ => null,
        };
        if (name is null) return null;

        uint mods = 0;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= Win;
        return Format(mods, name);
    }
}

/// <summary>
/// System-wide shortcuts (work even when Ember is hidden in the tray). Hotkeys are registered on a
/// dedicated thread with its own message loop, so they never depend on a visible window.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312, WmQuit = 0x0012, WmApply = 0x8001;
    private const uint ModNoRepeat = 0x4000;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private uint _threadId;
    private Dictionary<string, string> _pending = new();
    private readonly Dictionary<int, string> _idToAction = new();

    /// <summary>Raised on the UI thread with the action name, e.g. "toggle-overlay".</summary>
    public event Action<string>? Pressed;

    /// <summary>Raised on the UI thread after applying: actions whose shortcut another app already owns.</summary>
    public event Action<IReadOnlySet<string>>? Applied;

    public HotkeyService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Ember hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    public void Apply(IReadOnlyDictionary<string, string> bindings)
    {
        lock (_gate) _pending = new Dictionary<string, string>(bindings);
        if (_threadId != 0) PostThreadMessage(_threadId, WmApply, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // creates this thread's message queue
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmHotkey && _idToAction.TryGetValue((int)msg.wParam, out var action))
                Dispatcher.UIThread.Post(() => Pressed?.Invoke(action));
            else if (msg.message == WmApply)
                RegisterAll();
        }

        foreach (var id in _idToAction.Keys) UnregisterHotKey(IntPtr.Zero, id);
    }

    private void RegisterAll()
    {
        foreach (var id in _idToAction.Keys) UnregisterHotKey(IntPtr.Zero, id);
        _idToAction.Clear();

        Dictionary<string, string> bindings;
        lock (_gate) bindings = _pending;

        var failed = new HashSet<string>();
        int nextId = 1;
        foreach (var (action, text) in bindings)
        {
            var hotkey = Hotkey.Parse(text);
            if (hotkey is null) continue;
            var id = nextId++;
            if (RegisterHotKey(IntPtr.Zero, id, hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey))
                _idToAction[id] = action;
            else
                failed.Add(action);
        }
        Dispatcher.UIThread.Post(() => Applied?.Invoke(failed));
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
