using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal sealed partial class EditorCaret : Control
{
    private const double CaretWidth = 2;
    private const double FrameInterval = 1000d / 60;
    private const double GlideDuration = 100;
    private const double DefaultBlinkInterval = 530;
    private readonly TextArea _area;
    private readonly TextView _view;
    private readonly TimeProvider _clock;
    internal static readonly AttachedProperty<bool> SmoothProperty =
        AvaloniaProperty.RegisterAttached<EditorCaret, WindowBase, bool>("SmoothCaret", defaultValue: true);
    private WindowBase? _window;
    private CompositionSolidColorVisual? _cursor;
    private DispatcherTimer? _timer;
    private double _blinkInterval;
    private double _blinkTimeout;
    private long _lastActivity;
    private Rect _target;
    private double _displayX, _fromX, _inkFrom;
    private int _typingOffset = -1, _paintedOffset = -1;
    private bool _typingPending, _gliding;

    internal bool MotionEnabled { get; private set; }

    private EditorCaret(TextArea area, TimeProvider clock)
    {
        _area = area;
        _view = area.TextView;
        _clock = clock;
        IsHitTestVisible = false;
        area.GotFocus += (_, _) =>
        {
            // Keep AvaloniaEdit's caret position and IME, but stop its detached layer's blink timer.
            area.Caret.Hide();
            RefreshMotion();
        };
        area.LostFocus += (_, _) => ResetVisual();
        area.Caret.PositionChanged += (_, _) => Restart();
        area.SelectionChanged += (_, _) => Restart();
        area.DocumentChanged += (_, _) => ResetVisual();
        area.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextArea.OverstrikeModeProperty) Restart();
        };
        _view.ScrollOffsetChanged += (_, _) => ResetVisual();
        _view.VisualLinesChanged += (_, _) => InvalidateVisual();
        area.TextEntering += (_, e) => _typingOffset = IsDirectTyping(e) ? area.Caret.Offset : -1;
        area.TextEntered += (_, _) =>
        {
            if (_typingOffset < 0 || area.Caret.Offset <= _typingOffset) return;
            Restart();
            _typingPending = true;
        };
    }

    public static void Attach(TextArea area, TimeProvider? clock = null)
    {
        area.CaretBrush = FocusTheme.EditorCaret;
        area.TextView.InsertLayer(new EditorCaret(area, clock ?? TimeProvider.System), KnownLayer.Caret, LayerInsertionPosition.Replace);
    }

    private bool IsDirectTyping(TextInputEventArgs e) =>
        MotionEnabled && !_area.IsReadOnly && !_area.OverstrikeMode &&
        SingleCaret && _area.Selection.IsEmpty && _area.Caret.Offset == _paintedOffset &&
        // Clipboard handlers call PerformTextInput directly, without a routed source.
        e.Source is not null && e.Text is { Length: > 0 and <= 8 } text &&
        !text.AsSpan().ContainsAny('\r', '\n', '\t');

    private bool SingleCaret => _area.Selection is not RectangleSelection && MultiCaretEditing.Count(_area) == 1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this) is { } host)
        {
            _cursor = host.Compositor.CreateSolidColorVisual();
            ElementComposition.SetElementChildVisual(this, _cursor);
        }
        _window = TopLevel.GetTopLevel(this) as WindowBase;
        if (_window is not null)
        {
            _window.Activated += WindowActivityChanged;
            _window.Deactivated += WindowActivityChanged;
            _window.PropertyChanged += WindowPropertyChanged;
        }
        if (_area.IsKeyboardFocusWithin) RefreshMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ElementComposition.SetElementChildVisual(this, null);
        _cursor = null;
        if (_window is not null)
        {
            _window.Activated -= WindowActivityChanged;
            _window.Deactivated -= WindowActivityChanged;
            _window.PropertyChanged -= WindowPropertyChanged;
            _window = null;
        }
        ResetVisual();
        base.OnDetachedFromVisualTree(e);
    }

    private void WindowActivityChanged(object? sender, EventArgs e)
    {
        if (_window?.IsActive == true && _area.IsKeyboardFocusWithin) RefreshMotion();
        else ResetVisual();
    }

    private void WindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SmoothProperty && _area.IsKeyboardFocusWithin) RefreshMotion();
    }

    private void RefreshMotion()
    {
        const uint getClientAreaAnimation = 0x1042;
        const uint getCaretTimeout = 0x2022;
        var effects = !OperatingSystem.IsWindows() ||
            SystemParametersInfo(getClientAreaAnimation, 0, out var enabled, 0) == 0 || enabled != 0;
        MotionEnabled = effects && _window?.GetValue(SmoothProperty) != false;
        var blink = OperatingSystem.IsWindows() ? GetCaretBlinkTime() : 0;
        _blinkInterval = !effects || blink == uint.MaxValue ? 0 : blink == 0 ? DefaultBlinkInterval : blink;
        _blinkTimeout = OperatingSystem.IsWindows() && SystemParametersInfo(getCaretTimeout, 0, out var timeout, 0) != 0 && timeout > 0
            ? timeout : double.PositiveInfinity;
        Restart();
    }

    private void Restart()
    {
        _lastActivity = _clock.GetTimestamp();
        _typingPending = _gliding = false;
        InvalidateVisual();
    }

    private void ResetVisual()
    {
        _timer?.Stop();
        HideCaret();
        Restart();
    }

    public override void Render(DrawingContext drawingContext)
    {
        _timer?.Stop();
        if (_cursor is null || !_area.IsKeyboardFocusWithin || !IsEffectivelyVisible || _window?.IsActive == false || !_view.VisualLinesValid
            || _view.GetVisualLine(_area.Caret.Line) is null)
        {
            HideCaret();
            return;
        }

        var caret = _area.Caret.CalculateCaretRectangle().Translate(-_view.ScrollOffset);
        if (!new Rect(Bounds.Size).Intersects(caret.WithWidth(Math.Max(CaretWidth, caret.Width))))
        {
            HideCaret();
            return;
        }

        if (_typingPending)
        {
            _gliding = CanGlide(_target, caret);
            _fromX = _displayX;
            _inkFrom = _target.X;
            _typingPending = false;
        }
        else if (_target != caret)
        {
            _gliding = false;
        }
        _target = caret;
        _paintedOffset = _area.Caret.Offset;

        var scale = _window?.RenderScaling ?? 1;
        var brush = FocusTheme.EditorCaret;
        var elapsed = _clock.GetElapsedTime(_lastActivity).TotalMilliseconds;
        _displayX = caret.X;
        if (_gliding && elapsed < GlideDuration)
        {
            var remaining = 1 - elapsed / GlideDuration;
            _displayX += (_fromX - caret.X) * remaining * remaining * remaining;
            using (drawingContext.PushOpacity(0.28 * remaining))
                drawingContext.FillRectangle(brush,
                    new Rect(Math.Min(_inkFrom, caret.X), caret.Bottom - 2, Math.Abs(caret.X - _inkFrom), 1.5), 0.75f);
        }
        else _gliding = false;
        var rect = _area.OverstrikeMode ? caret : SnapLine(caret.WithX(_displayX), scale);
        _cursor.Offset = new(rect.X, rect.Y, 0);
        _cursor.Size = new(rect.Width, rect.Height);
        _cursor.Color = _area.OverstrikeMode ? Color.FromArgb(100, brush.Color.R, brush.Color.G, brush.Color.B) : brush.Color;
        _cursor.Visible = true;
        UpdateBlink();
    }

    private void HideCaret()
    {
        _target = default;
        _paintedOffset = -1;
        if (_cursor is not null) _cursor.Visible = false;
    }

    private void UpdateBlink()
    {
        if (_cursor is null || !_cursor.Visible) return;
        var elapsed = _clock.GetElapsedTime(_lastActivity).TotalMilliseconds;
        var blink = _blinkInterval > 0 && SingleCaret && elapsed < _blinkTimeout;
        var (opacity, wait) = blink ? BlinkFrame(elapsed, _blinkInterval, MotionEnabled) : (1d, double.PositiveInfinity);
        _cursor.Opacity = (float)opacity;
        if (double.IsFinite(wait)) wait = Math.Min(wait, _blinkTimeout - elapsed);
        if (_gliding) wait = Math.Min(wait, FrameInterval);
        if (double.IsFinite(wait)) Schedule(wait);
    }

    internal static bool CanGlide(Rect from, Rect to) =>
        from.Height > 0 && from.Y == to.Y && from.Height == to.Height && Math.Abs(from.X - to.X) is > 0 and <= 64;

    internal static (double Opacity, double Wait) BlinkFrame(double elapsed, double interval, bool smooth = true)
    {
        var phase = elapsed % (2 * interval);
        var time = phase % interval;
        if (!smooth) return (phase < interval ? 1 : 0, interval - time);
        const double dim = 0.12;
        var fade = Math.Min(160, interval / 2);
        var hold = interval - fade;
        var t = Math.Max(0, (time - hold) / fade);
        var eased = t * t * (3 - 2 * t);
        // Four opacity steps per fade; only movement needs 60 frames per second.
        return (phase < interval ? 1 - (1 - dim) * eased : dim + (1 - dim) * eased,
            time < hold ? hold - time + fade / 4 : Math.Min(fade / 4, interval - time));
    }

    private void Schedule(double wait)
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                if (_gliding) InvalidateVisual();
                else UpdateBlink();
            };
        }
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, wait));
        _timer.Start();
    }

    internal static void DrawLine(DrawingContext drawingContext, Rect caret, IBrush brush, double scale)
        => drawingContext.FillRectangle(brush, SnapLine(caret, scale));

    private static Rect SnapLine(Rect caret, double scale)
    {
        var x = Math.Round(caret.X * scale) / scale;
        var y = Math.Round(caret.Y * scale) / scale;
        var bottom = Math.Round(caret.Bottom * scale) / scale;
        var width = Math.Ceiling(CaretWidth * scale) / scale;
        return new Rect(x, y, width, Math.Max(0, bottom - y));
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetCaretBlinkTime();
}
