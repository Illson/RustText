using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private WindowBase? _window;
    private DispatcherTimer? _timer;
    private double _blinkInterval;
    private long _lastActivity;
    private Rect _target;
    private double _displayX, _fromX, _inkFrom;
    private int _typingOffset = -1;
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
        MultiCaretEditing.Count(_area) == 1 && _area.Selection.IsEmpty &&
        // Clipboard handlers call PerformTextInput directly, without a routed source.
        e.Source is not null && e.Text is { Length: > 0 and <= 8 } text &&
        !text.AsSpan().ContainsAny('\r', '\n', '\t');

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as WindowBase;
        if (_window is not null)
        {
            _window.Activated += WindowActivityChanged;
            _window.Deactivated += WindowActivityChanged;
        }
        if (_area.IsKeyboardFocusWithin) RefreshMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= WindowActivityChanged;
            _window.Deactivated -= WindowActivityChanged;
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

    private void RefreshMotion()
    {
        const uint getClientAreaAnimation = 0x1042;
        MotionEnabled = !OperatingSystem.IsWindows() ||
            SystemParametersInfo(getClientAreaAnimation, 0, out var enabled, 0) == 0 || enabled != 0;
        var blink = OperatingSystem.IsWindows() ? GetCaretBlinkTime() : 0;
        _blinkInterval = !MotionEnabled || blink == uint.MaxValue ? 0 : blink == 0 ? DefaultBlinkInterval : blink;
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
        _target = default;
        Restart();
    }

    public override void Render(DrawingContext drawingContext)
    {
        _timer?.Stop();
        if (!_area.IsKeyboardFocusWithin || !IsEffectivelyVisible || _window?.IsActive == false || !_view.VisualLinesValid
            || _view.GetVisualLine(_area.Caret.Line) is null)
        {
            _target = default;
            return;
        }

        var caret = _area.Caret.CalculateCaretRectangle().Translate(-_view.ScrollOffset);
        if (!new Rect(Bounds.Size).Intersects(caret.WithWidth(Math.Max(CaretWidth, caret.Width))))
        {
            _target = default;
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

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var brush = FocusTheme.EditorCaret;
        var elapsed = _clock.GetElapsedTime(_lastActivity).TotalMilliseconds;
        var (opacity, wait) = _blinkInterval > 0 && MultiCaretEditing.Count(_area) == 1
            ? BlinkFrame(elapsed, _blinkInterval) : (1d, double.PositiveInfinity);
        _displayX = caret.X;
        if (_gliding && elapsed < GlideDuration)
        {
            var remaining = 1 - elapsed / GlideDuration;
            _displayX += (_fromX - caret.X) * remaining * remaining * remaining;
            using (drawingContext.PushOpacity(0.28 * remaining))
                drawingContext.FillRectangle(brush,
                    new Rect(Math.Min(_inkFrom, caret.X), caret.Bottom - 2, Math.Abs(caret.X - _inkFrom), 1.5), 0.75f);
            wait = Math.Min(wait, FrameInterval);
        }

        using (drawingContext.PushOpacity(_area.OverstrikeMode ? opacity * 0.39 : opacity))
        {
            if (_area.OverstrikeMode) drawingContext.FillRectangle(brush, caret);
            else DrawLine(drawingContext, caret.WithX(_displayX), brush, scale);
        }
        if (double.IsFinite(wait)) Schedule(wait);
    }

    internal static bool CanGlide(Rect from, Rect to) =>
        from.Height > 0 && from.Y == to.Y && from.Height == to.Height && Math.Abs(from.X - to.X) is > 0 and <= 64;

    internal static (double Opacity, double Wait) BlinkFrame(double elapsed, double interval)
    {
        const double dim = 0.12;
        var phase = elapsed % (2 * interval);
        var time = phase % interval;
        var fade = Math.Min(160, interval / 2);
        var hold = interval - fade;
        var t = Math.Max(0, (time - hold) / fade);
        var eased = t * t * (3 - 2 * t);
        return (phase < interval ? 1 - (1 - dim) * eased : dim + (1 - dim) * eased,
            time < hold ? hold - time : FrameInterval);
    }

    private void Schedule(double wait)
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                InvalidateVisual();
            };
        }
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, wait));
        _timer.Start();
    }

    internal static void DrawLine(DrawingContext drawingContext, Rect caret, IBrush brush, double scale)
    {
        var x = Math.Round(caret.X * scale) / scale;
        var y = Math.Round(caret.Y * scale) / scale;
        var bottom = Math.Round(caret.Bottom * scale) / scale;
        var width = Math.Ceiling(CaretWidth * scale) / scale;
        drawingContext.FillRectangle(brush, new Rect(x, y, width, Math.Max(0, bottom - y)));
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetCaretBlinkTime();
}
