using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Ember.Services;

/// <summary>Small Win32 helpers: process image paths and exe icons, without pulling in WinForms.</summary>
internal static class Native
{
    // ---------- Process path ----------

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder buffer, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Full exe path for a process. Works for most processes without admin.</summary>
    public static string? GetProcessPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------- Windows (to tell "apps" from background processes) ----------

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>
    /// PIDs that own a real, visible top-level window (what Task Manager calls "Apps"):
    /// visible, unowned, titled, not a tool window, and not cloaked (hidden UWP frames).
    /// </summary>
    public static HashSet<int> GetPidsWithWindows()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            if (((long)GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            try
            {
                if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;
            }
            catch (DllNotFoundException) { }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0) pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    // ---------- Icons ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>The exe's own icon (32px, shown at 18px so it stays sharp on high-DPI screens).</summary>
    public static Bitmap? ExtractIcon(string path, int index = 0)
    {
        var large = new IntPtr[1];
        if (ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try { return IconToBitmap(large[0], 32); }
        finally { DestroyIcon(large[0]); }
    }

    /// <summary>Generic "application" icon for processes with no readable exe.</summary>
    public static Bitmap? GenericAppIcon() =>
        ExtractIcon(Path.Combine(Environment.SystemDirectory, "shell32.dll"), 2);

    private static Bitmap? IconToBitmap(IntPtr icon, int size)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,   // top-down rows
            Planes = 1,
            BitCount = 32,
        };

        var hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;

        var dib = CreateDIBSection(hdc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(hdc);
            return null;
        }

        var previous = SelectObject(hdc, dib);
        try
        {
            DrawIconEx(hdc, 0, 0, icon, size, size, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);

            int length = size * size * 4;
            var pixels = new byte[length];
            Marshal.Copy(bits, pixels, 0, length);

            // Very old icons have no alpha channel; make their drawn pixels opaque.
            bool hasAlpha = false;
            for (int i = 3; i < length; i += 4)
                if (pixels[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
                for (int i = 0; i < length; i += 4)
                    if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) pixels[i + 3] = 255;

            var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = bitmap.Lock())
            {
                for (int y = 0; y < size; y++)
                    Marshal.Copy(pixels, y * size * 4, frame.Address + y * frame.RowBytes, size * 4);
            }
            return bitmap;
        }
        finally
        {
            SelectObject(hdc, previous);
            DeleteObject(dib);
            DeleteDC(hdc);
        }
    }
}
