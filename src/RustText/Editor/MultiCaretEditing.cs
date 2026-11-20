using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal readonly record struct CaretRange(int Anchor, int Caret)
{
    public int Start => Math.Min(Anchor, Caret);
    public int End => Math.Max(Anchor, Caret);
    public int Length => End - Start;
}

internal readonly record struct CaretEdit(CaretRange Range, string Text);

internal sealed class MultiCaretEditing : IDisposable, IBackgroundRenderer
{
    internal const int MaxCarets = 10_000;
    private readonly TextArea _area;
    private readonly Func<IReadOnlyList<CaretEdit>, bool> _canEdit;
    private readonly Action<string> _status;
    private CaretClick? _caretClick;
    private TextDocument Document => _area.Document;
    private MultiSelection? Current => _area.Selection as MultiSelection;
    public bool IsActive => Current is not null;
    public static int Count(TextArea area) => area.Selection is MultiSelection multi ? multi.Ranges.Length : 1;

    public MultiCaretEditing(TextArea area, Func<IReadOnlyList<CaretEdit>, bool> canEdit, Action<string> status)
    {
        _area = area;
        _canEdit = canEdit;
        _status = status;
        area.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerMovedEvent, PointerMoved, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        area.AddHandler(VisualLineLinkText.OpenUriEvent, OpenUri);
        area.GotFocus += FocusChanged;
        area.LostFocus += FocusChanged;
        area.TextView.BackgroundRenderers.Add(this);
    }

    public void Dispose()
    {
        _area.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
        _area.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        _area.RemoveHandler(InputElement.PointerMovedEvent, PointerMoved);
        _area.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
        _area.RemoveHandler(VisualLineLinkText.OpenUriEvent, OpenUri);
        _area.GotFocus -= FocusChanged;
        _area.LostFocus -= FocusChanged;
        _area.TextView.BackgroundRenderers.Remove(this);
    }

    public static CaretRange Primary(TextArea area)
    {
        if (area.Selection is MultiSelection multi) return multi.Primary;
        if (area.Selection.IsEmpty) return new(area.Caret.Offset, area.Caret.Offset);
        var segment = area.Selection.SurroundingSegment;
        return area.Caret.Offset < segment.EndOffset
            ? new(segment.EndOffset, segment.Offset)
            : new(segment.Offset, segment.EndOffset);
    }

    public static void CollapseToPrimary(TextArea area)
    {
        if (area.Selection is not MultiSelection multi) return;
        var primary = multi.Primary;
        area.Caret.Offset = primary.Caret;
        area.Selection = Selection.Create(area, primary.Anchor, primary.Caret);
    }

    public void SelectMatches(IReadOnlyList<CaretRange> ranges)
    {
        if (ranges.Count == 0) return;
        var primary = 0;
        var caret = _area.Caret.Offset;
        for (var index = 0; index < ranges.Count; index++)
        {
            if (ranges[index].End < caret) continue;
            primary = index;
            break;
        }
        SetCarets(ranges, primary);
        _area.Focus();
    }

    public void Paste(string text)
    {
        if (Current is not { } selection) return;
        // A clipboard with one line per selection is distributed in document order.
        // Count before splitting, so a large paste never allocates millions of fragments.
        var lineCount = 1;
        foreach (var character in text)
        {
            if (character == '\n' && ++lineCount > selection.Ranges.Length) break;
        }
        if (lineCount == selection.Ranges.Length)
        {
            var lines = text.Split('\n');
            Apply(selection, selection.Ranges.Select((range, index) => new CaretEdit(range, lines[index])).ToArray());
        }
        else
        {
            selection.ReplaceSelectionWithText(text);
        }
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        const KeyModifiers controlAlt = KeyModifiers.Control | KeyModifiers.Alt;
        var modifiers = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Up or Key.Down when modifiers == controlAlt:
                AddVertical(e.Key == Key.Up ? -1 : 1);
                break;
            case Key.D when modifiers == KeyModifiers.Control:
                SelectOccurrences(all: false);
                break;
            case Key.L when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                SelectOccurrences(all: true);
                break;
            case Key.Escape when Current is not null:
                CollapseToPrimary(_area);
                break;
            default:
                if (Current is not { } selection || !EditOrMove(selection, e.Key, modifiers)) return;
                break;
        }
        e.Handled = true;
    }

    private void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _caretClick = null;
        var view = _area.TextView;
        if (e.Source is not Visual source || !view.IsVisualAncestorOf(source) && !ReferenceEquals(view, source)
            || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Alt)
        {
            var point = e.GetPosition(view);
            if (view.GetPosition(point + view.ScrollOffset) is not { } position) return;

            // AvaloniaEdit handles drags, including rectangular selection with Alt.
            // A click restores the saved carets and toggles the one at the clicked position.
            _caretClick = new(e.Pointer, point, e.KeyModifiers, Document.Version, Document.GetOffset(position.Location),
                Current?.Ranges ?? [Primary(_area)], Current?.PrimaryIndex ?? 0);
            _area.ClearSelection();
            return;
        }

        CollapseToPrimary(_area);
    }

    private void PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_caretClick is { } click && click.Pointer == e.Pointer && MovedSincePress(e, click))
            _caretClick = null;
    }

    private void OpenUri(object? sender, OpenUriRoutedEventArgs e)
    {
        // AvaloniaEdit opens links on mouse down with any Control modifier combination.
        // Reserve exact Ctrl+click for carets. Ctrl+Shift+click still opens a link.
        if (_caretClick?.Modifiers == KeyModifiers.Control) e.Handled = true;
    }

    private void PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_caretClick is not { } click || click.Pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        _caretClick = null;
        if (MovedSincePress(e, click) || !ReferenceEquals(click.Version, Document.Version)) return;

        var ranges = click.Ranges;
        var existing = Array.FindIndex(ranges, range => click.Offset >= range.Start && click.Offset <= range.End);
        if (existing < 0)
        {
            SetCarets([.. ranges, new(click.Offset, click.Offset)], ranges.Length);
        }
        else if (ranges.Length > 1)
        {
            var remaining = ranges.Where((_, index) => index != existing).ToArray();
            var primary = click.PrimaryIndex == existing ? Math.Min(existing, remaining.Length - 1)
                : existing < click.PrimaryIndex ? click.PrimaryIndex - 1 : click.PrimaryIndex;
            SetCarets(remaining, primary);
        }
        else
        {
            SetCarets(ranges, primaryIndex: 0);
        }

        _area.Focus();
        e.Handled = true;
    }

    private bool MovedSincePress(PointerEventArgs e, CaretClick click)
    {
        var movement = e.GetPosition(_area.TextView) - click.Point;
        return Math.Abs(movement.X) > 2 || Math.Abs(movement.Y) > 2;
    }

    private sealed record CaretClick(IPointer Pointer, Point Point, KeyModifiers Modifiers, ITextSourceVersion Version, int Offset,
        CaretRange[] Ranges, int PrimaryIndex);

    private void AddVertical(int direction)
    {
        var primary = Primary(_area);
        var ranges = Current?.Ranges ?? [primary];
        var outer = direction < 0 ? ranges[0] : ranges[^1];
        var line = Document.GetLineByOffset(outer.Caret);
        var target = line.LineNumber + direction;
        if (target < 1 || target > Document.LineCount) return;
        var column = Current?.PreferredColumn ?? primary.Caret - Document.GetLineByOffset(primary.Caret).Offset;
        var offset = OffsetAtColumn(Document.GetLineByNumber(target), column);
        SetCarets([.. ranges, new(offset, offset)], ranges.Length, column);
    }

    private int OffsetAtColumn(DocumentLine line, int column)
    {
        var offset = line.Offset + Math.Min(column, line.Length);
        if (offset == line.Offset || offset == line.EndOffset) return offset;
        return Math.Min(line.EndOffset, Step(offset - 1, forward: true, CaretPositioningMode.Normal));
    }

    private void SelectOccurrences(bool all)
    {
        var primary = Primary(_area);
        var ranges = Current?.Ranges ?? [primary];
        if (primary.Length == 0)
        {
            var offset = primary.Caret;
            if (offset == Document.TextLength || !IsWordCharacter(Document.GetCharAt(offset)))
            {
                if (offset == 0 || !IsWordCharacter(Document.GetCharAt(offset - 1))) return;
                offset--;
            }
            var start = offset;
            var end = offset + 1;
            while (start > 0 && IsWordCharacter(Document.GetCharAt(start - 1))) start--;
            while (end < Document.TextLength && IsWordCharacter(Document.GetCharAt(end))) end++;
            primary = new(start, end);
            if (!all)
            {
                SetCarets([.. ranges, primary], ranges.Length);
                return;
            }
        }

        var text = Document.Text;
        var query = text.Substring(primary.Start, primary.Length);
        if (all)
        {
            var matches = new List<CaretRange> { primary };
            CollectMatches(primary.End, text.Length);
            CollectMatches(0, primary.Start);
            SetCarets(matches, primaryIndex: 0);
            return;

            void CollectMatches(int start, int end)
            {
                for (var offset = Find(start, end); offset >= 0 && matches.Count <= MaxCarets; offset = Find(offset + query.Length, end))
                {
                    matches.Add(new(offset, offset + query.Length));
                }
            }
        }

        // Search from the last added occurrence, wrapping around once.
        var next = FindAvailable(primary.End, text.Length);
        if (next < 0) next = FindAvailable(0, primary.Start);
        if (next >= 0) SetCarets([.. ranges, new(next, next + query.Length)], ranges.Length);

        int Find(int start, int end) => text.IndexOf(query, start, end - start, StringComparison.Ordinal);

        int FindAvailable(int start, int end)
        {
            var occupied = 0;
            for (var offset = Find(start, end); offset >= 0; offset = Find(offset + 1, end))
            {
                while (occupied < ranges.Length && ranges[occupied].End <= offset) occupied++;
                if (occupied == ranges.Length || ranges[occupied].Start >= offset + query.Length) return offset;
            }
            return -1;
        }
    }

    private static bool IsWordCharacter(char character) =>
        TextUtilities.GetCharacterClass(character) == CharacterClass.IdentifierPart;

    private bool EditOrMove(MultiSelection selection, Key key, KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var control = modifiers.HasFlag(KeyModifiers.Control);
        if ((modifiers & ~(KeyModifiers.Shift | KeyModifiers.Control)) != 0) return false;
        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            if (control && key is Key.Up or Key.Down) return false;
            SetCarets(selection.Ranges.Select(Move).ToArray(), selection.PrimaryIndex);
            return true;
        }
        if (key is Key.Back or Key.Delete && (key == Key.Back || !shift))
        {
            if (!_area.IsReadOnly) Apply(selection, selection.Ranges.Select(Delete).ToArray());
            return true;
        }
        if (key == Key.Tab && modifiers == KeyModifiers.None)
        {
            if (!_area.IsReadOnly)
            {
                var indent = _area.Options.IndentationSize;
                Apply(selection, selection.Ranges.Select(range => new CaretEdit(range, _area.Options.ConvertTabsToSpaces
                    ? new string(' ', indent - (Document.GetLocation(range.Start).Column - 1) % indent)
                    : "\t")).ToArray());
            }
            return true;
        }
        return false;

        CaretRange Move(CaretRange range)
        {
            var line = Document.GetLineByOffset(range.Caret);
            var target = key switch
            {
                Key.Left when !shift && !control && range.Length > 0 => range.Start,
                Key.Right when !shift && !control && range.Length > 0 => range.End,
                Key.Left or Key.Right => Step(range.Caret, key == Key.Right,
                    control ? CaretPositioningMode.WordStart : CaretPositioningMode.Normal),
                Key.Home => control ? 0 : line.Offset,
                Key.End => control ? Document.TextLength : line.EndOffset,
                Key.Up or Key.Down => OffsetAtColumn(
                    Document.GetLineByNumber(Math.Clamp(line.LineNumber + (key == Key.Up ? -1 : 1), 1, Document.LineCount)),
                    range.Caret - line.Offset),
                _ => range.Caret
            };
            return new(shift ? range.Anchor : target, target);
        }

        CaretEdit Delete(CaretRange range)
        {
            if (range.Length != 0) return new(range, "");
            var target = Step(range.Caret, key == Key.Delete,
                control ? CaretPositioningMode.WordStart : CaretPositioningMode.Normal);
            return new(new(range.Caret, target), "");
        }
    }

    private int Step(int offset, bool forward, CaretPositioningMode mode)
    {
        var next = TextUtilities.GetNextCaretPosition(Document, offset,
            forward ? LogicalDirection.Forward : LogicalDirection.Backward, mode);
        return next >= 0 ? next : forward ? Document.TextLength : 0;
    }

    private void SetCarets(IReadOnlyList<CaretRange> ranges, int primaryIndex, int? preferredColumn = null)
    {
        var (normalized, primary) = Normalize(ranges, primaryIndex);
        if (normalized.Length > MaxCarets)
        {
            _status($"Слишком много курсоров. Допустимо до {LineNumbers.Format(MaxCarets)}.");
            return;
        }
        _area.Caret.Offset = normalized[primary].Caret;
        _area.Selection = normalized.Length == 1
            ? Selection.Create(_area, normalized[0].Anchor, normalized[0].Caret)
            : new MultiSelection(this, normalized, primary, preferredColumn);
        _area.Caret.BringCaretToView();
    }

    private void Apply(MultiSelection selection, CaretEdit[] edits)
    {
        if (_area.IsReadOnly || !edits.Any(edit => edit.Text.Length > 0 || edit.Range.Length > 0)) return;
        var primaryIndex = selection.PrimaryIndex;
        if (edits.All(edit => edit.Text.Length == 0))
        {
            // Backspace and Delete can expand neighboring carets into overlapping ranges.
            var (ranges, primary) = Normalize(edits.Select(edit => edit.Range).ToArray(), primaryIndex);
            edits = ranges.Select(range => new CaretEdit(range, "")).ToArray();
            primaryIndex = primary;
        }
        if (!_canEdit(edits))
        {
            _status("Операция с несколькими курсорами превышает лимит редактора.");
            return;
        }
        Replace(edits, primaryIndex);
    }

    private void Replace(CaretEdit[] edits, int primaryIndex)
    {
        var carets = new CaretRange[edits.Length];
        var delta = 0;
        for (var index = 0; index < edits.Length; index++)
        {
            var (range, text) = edits[index];
            var caret = range.Start + delta + text.Length;
            carets[index] = new(caret, caret);
            delta += text.Length - range.Length;
        }
        using (Document.RunUpdate())
        {
            // Keep the old selection in AvaloniaEdit's undo record, then avoid remapping
            // all carets once per replacement. Apply edits at original offsets from right to left.
            Document.UndoStack.PushOptional(new CaretUndoAction(this, undo: null,
                redo: static owner => owner._area.ClearSelection()));
            _area.ClearSelection();
            for (var index = edits.Length - 1; index >= 0; index--)
            {
                var (range, text) = edits[index];
                if (range.Length > 0 || text.Length > 0) Document.Replace(range.Start, range.Length, text);
            }
            SetCarets(carets, primaryIndex);
            var after = _area.Selection;
            var offsetAfter = _area.Caret.Offset;
            Document.UndoStack.PushOptional(new CaretUndoAction(this,
                undo: static owner => owner._area.ClearSelection(),
                redo: owner =>
                {
                    owner._area.Caret.Offset = offsetAfter;
                    owner._area.Selection = after;
                }));
        }
    }

    private static (CaretRange[] Ranges, int PrimaryIndex) Normalize(IReadOnlyList<CaretRange> ranges, int primaryIndex)
    {
        var primary = ranges[primaryIndex];
        var normalized = new List<CaretRange>(ranges.Count);
        foreach (var range in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (normalized.Count == 0)
            {
                normalized.Add(range);
                continue;
            }
            var previous = normalized[^1];
            if (range.Start > previous.End || range.Start == previous.End && previous.Length > 0 && range.Length > 0)
            {
                normalized.Add(range);
                continue;
            }
            var end = Math.Max(previous.End, range.End);
            var direction = range == primary ? range : previous;
            normalized[^1] = direction.Caret < direction.Anchor ? new(end, previous.Start) : new(previous.Start, end);
        }
        var ordered = normalized.ToArray();
        var position = Array.FindIndex(ordered, range => range.Start <= primary.Start && range.End >= primary.End);
        return (ordered, position);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    private void FocusChanged(object? sender, RoutedEventArgs args) => _area.TextView.InvalidateLayer(Layer);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!_area.IsKeyboardFocusWithin || Current is not { } selection || !textView.VisualLinesValid) return;
        var scroll = textView.ScrollOffset;
        var scale = TopLevel.GetTopLevel(textView)?.RenderScaling ?? 1;
        var brush = FocusTheme.EditorCaret;
        for (var index = 0; index < selection.Ranges.Length; index++)
        {
            if (index == selection.PrimaryIndex) continue;
            var location = Document.GetLocation(selection.Ranges[index].Caret);
            if (textView.GetVisualLine(location.Line) is null) continue;
            var position = new TextViewPosition(location);
            var top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - scroll;
            var bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - scroll;
            EditorCaret.DrawLine(drawingContext, new Rect(top.X, top.Y, 0, bottom.Y - top.Y), brush, scale);
        }
    }

    private sealed class CaretUndoAction(MultiCaretEditing owner, Action<MultiCaretEditing>? undo, Action<MultiCaretEditing>? redo)
        : IUndoableOperation
    {
        private readonly WeakReference<MultiCaretEditing> _owner = new(owner);
        private readonly WeakReference<TextDocument> _document = new(owner.Document);

        public void Undo() => Run(undo);
        public void Redo() => Run(redo);

        private void Run(Action<MultiCaretEditing>? action)
        {
            if (action is not null && _owner.TryGetTarget(out var editor) && _document.TryGetTarget(out var document)
                && ReferenceEquals(editor.Document, document))
            {
                action(editor);
            }
        }
    }

    private sealed class MultiSelection(MultiCaretEditing owner, CaretRange[] ranges, int primaryIndex, int? preferredColumn)
        : Selection(owner._area)
    {
        public CaretRange[] Ranges { get; } = ranges;
        public int PrimaryIndex { get; } = primaryIndex;
        public int? PreferredColumn { get; } = preferredColumn;
        public CaretRange Primary => Ranges[PrimaryIndex];
        public override TextViewPosition StartPosition => new(owner.Document.GetLocation(Primary.Anchor));
        public override TextViewPosition EndPosition => new(owner.Document.GetLocation(Primary.Caret));
        public override int Length => Ranges.Sum(range => range.Length);
        public override ISegment SurroundingSegment => new SimpleSegment(Ranges[0].Start, Ranges[^1].End - Ranges[0].Start);
        public override IEnumerable<SelectionSegment> Segments => Ranges.Select(range => new SelectionSegment(range.Start, range.End));
        public override string GetText() => string.Join('\n', Ranges.Select(range => owner.Document.GetText(range.Start, range.Length)));
        public override void ReplaceSelectionWithText(string newText) =>
            owner.Apply(this, Ranges.Select(range => new CaretEdit(range, newText)).ToArray());

        public override Selection SetEndpoint(TextViewPosition end) =>
            Create(owner._area, Primary.Anchor, owner.Document.GetOffset(end.Location));

        public override Selection StartSelectionOrSetEndpoint(TextViewPosition start, TextViewPosition end) =>
            Create(owner._area, IsEmpty ? owner.Document.GetOffset(start.Location) : Primary.Anchor, owner.Document.GetOffset(end.Location));

        public override Selection UpdateOnDocumentChange(DocumentChangeEventArgs e)
        {
            var updated = Ranges.Select(range => new CaretRange(e.GetNewOffset(range.Anchor), e.GetNewOffset(range.Caret))).ToArray();
            var (normalized, primary) = Normalize(updated, PrimaryIndex);
            return normalized.Length == 1
                ? Create(owner._area, normalized[0].Anchor, normalized[0].Caret)
                : new MultiSelection(owner, normalized, primary, null);
        }

        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}
