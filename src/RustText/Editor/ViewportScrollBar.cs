using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace RustText.Demo.Editor;

internal sealed class ViewportScrollBar : ScrollBar
{
    private ScrollViewer? _owner;
    private bool _synchronizing;

    public ViewportScrollBar()
    {
        Orientation = Orientation.Vertical;
        AllowAutoHide = false;
        Visibility = ScrollBarVisibility.Auto;
        Maximum = 0;
        ViewportSize = 0;
        AutomationProperties.SetName(this, "Вертикальная прокрутка");
    }

    internal void SetOwner(ScrollViewer? owner)
    {
        if (ReferenceEquals(_owner, owner)) return;
        if (_owner is not null) _owner.PropertyChanged -= OnOwnerPropertyChanged;
        _owner = owner;
        if (owner is not null) owner.PropertyChanged += OnOwnerPropertyChanged;
        Synchronize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_synchronizing && _owner is { } owner)
            owner.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(owner.Offset.X, Value));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || _owner?.Presenter is not { } presenter || VisualRoot is not Visual root) return;

        var point = e.GetCurrentPoint(root);
        e.Handled = true;
        presenter.RaiseEvent(new PointerWheelEventArgs(presenter, e.Pointer, root,
            point.Position, e.Timestamp, point.Properties, e.KeyModifiers, e.Delta));
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ScrollViewer.OffsetProperty || change.Property == ScrollViewer.ExtentProperty
            || change.Property == ScrollViewer.ViewportProperty || change.Property == ScrollViewer.SmallChangeProperty
            || change.Property == ScrollViewer.LargeChangeProperty)
            Synchronize();
    }

    private void Synchronize()
    {
        // Range changes may coerce the thumb value. Only input from the bar should move the owner.
        _synchronizing = true;
        try
        {
            Maximum = _owner?.ScrollBarMaximum.Y ?? 0;
            ViewportSize = _owner?.Viewport.Height ?? 0;
            SmallChange = _owner?.SmallChange.Height ?? 0;
            LargeChange = _owner?.LargeChange.Height ?? 0;
            Value = _owner?.Offset.Y ?? 0;
        }
        finally { _synchronizing = false; }
    }
}
