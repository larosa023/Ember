using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ember.Views;

/// <summary>
/// Small always-on-top stats panel. Drag to move. When locked it becomes click-through
/// (WS_EX_TRANSPARENT) so it never gets in the way of a game.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x8000000;

    public OverlayWindow()
    {
        InitializeComponent();
        Card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        Opened += (_, _) => ApplyExStyle(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;   // never minimize or maximize
    }

    /// <summary>Hidden from Alt+Tab, never steals focus; click-through when locked.</summary>
    public void ApplyExStyle(bool locked)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        style |= WsExToolWindow | WsExNoActivate | WsExLayered;
        style = locked ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
