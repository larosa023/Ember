using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ember.ViewModels;

namespace Ember.Controls;

/// <summary>
/// The thin divider after a column header. Drag to resize that column; double-click to reset it.
/// </summary>
public class ColumnResizer : Border
{
    public static readonly StyledProperty<string> ColumnProperty =
        AvaloniaProperty.Register<ColumnResizer, string>(nameof(Column), "");

    private bool _dragging;
    private double _startX;
    private double _startWidth;

    public ColumnResizer()
    {
        Width = 9;
        Background = Brushes.Transparent;   // whole 9px strip is grabbable, not just the 1px line
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    public string Column
    {
        get => GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ColumnLayout.Instance.Reset(Column);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _startX = e.GetPosition(TopLevel.GetTopLevel(this)).X;
        _startWidth = ColumnLayout.Instance.Get(Column);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var x = e.GetPosition(TopLevel.GetTopLevel(this)).X;
        ColumnLayout.Instance.Set(Column, _startWidth + (x - _startX));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging) EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        ColumnLayout.Instance.Save();
    }
}
