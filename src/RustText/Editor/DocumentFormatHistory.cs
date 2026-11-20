using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AvaloniaEdit.Document;

namespace RustText.Demo.Editor;

internal sealed record EditorFileFormat(TextEncodingKind Encoding, LineEndingKind Ending,
    ImmutableList<LineEndingKind>? MixedEndings = null)
{
    public static EditorFileFormat From(TextEncodingKind encoding, LineEndingKind ending, IEnumerable<LineEndingKind>? endings) =>
        new(encoding, ending, ending == LineEndingKind.Mixed ? endings?.ToImmutableList() ?? [] : null);
}

// One format and undo history belong to the document, including when several tabs share it.
internal sealed class DocumentFormatHistory
{
    private static readonly ConditionalWeakTable<TextDocument, DocumentFormatHistory> Histories = new();
    private readonly TextDocument _document;
    private bool _restoringFormat;
    private (int First, int Removed, int OldLineCount)? _pending;
    public EditorFileFormat Current { get; private set; }
    public event Action<EditorFileFormat>? Changed;

    public static DocumentFormatHistory For(TextDocument document, EditorFileFormat initial) =>
        Histories.GetValue(document, key => new(key, initial));

    private DocumentFormatHistory(TextDocument document, EditorFileFormat initial)
    {
        _document = document;
        // Older checkpoints may still contain the original file's pre-edit map.
        if (initial.MixedEndings is { } map && map.Count != document.LineCount - 1)
        {
            var count = document.LineCount - 1;
            initial = initial with { MixedEndings = map.Count > count ? map.RemoveRange(count, map.Count - count)
                : map.AddRange(Enumerable.Repeat(LineEndingKind.Lf, count - map.Count)) };
        }
        Current = initial;
        document.Changing += BeforeTextChange;
        document.Changed += AfterTextChange;
    }

    private void BeforeTextChange(object? sender, DocumentChangeEventArgs e)
    {
        if (_restoringFormat || Current.MixedEndings is null || !_document.UndoStack.AcceptChanges) return;
        var offset = e.Offset;
        var oldEnd = offset + e.RemovalLength;
        // Some editing commands replace a whole selection even when its edges are unchanged.
        if (e.RemovalLength != 0 && e.InsertionLength != 0)
        {
            var oldText = e.RemovedText.Text.AsSpan();
            var newText = e.InsertedText.Text.AsSpan();
            var prefix = oldText.CommonPrefixLength(newText);
            offset += prefix;
            var suffixLimit = Math.Min(oldText.Length, newText.Length) - prefix;
            for (var i = 1; i <= suffixLimit && oldText[^i] == newText[^i]; i++) oldEnd--;
        }
        var first = _document.GetLineByOffset(offset).LineNumber - 1;
        var last = _document.GetLineByOffset(oldEnd).LineNumber - 1;
        _pending = (first, last - first, _document.LineCount);
    }

    private void AfterTextChange(object? sender, DocumentChangeEventArgs e)
    {
        if (_pending is not { } change) return;
        _pending = null;
        var delta = _document.LineCount - change.OldLineCount;
        if (delta == 0) return;
        var map = Current.MixedEndings!;
        // Retain existing delimiters in the replaced span and all delimiters outside the edit.
        var end = Math.Clamp(change.First + change.Removed, Math.Max(0, -delta), map.Count);
        map = delta < 0 ? map.RemoveRange(end + delta, -delta)
            : map.InsertRange(end, Enumerable.Repeat(LineEndingKind.Lf, delta));
        SetUndoable(Current with { MixedEndings = map });
    }

    public void Restore(string text, EditorFileFormat format)
    {
        using var update = _document.RunUpdate();
        _restoringFormat = true;
        try
        {
            _document.Replace(0, _document.TextLength, text);
            SetUndoable(format);
        }
        finally { _restoringFormat = false; }
    }

    private void SetUndoable(EditorFileFormat format)
    {
        if (Current == format) return;
        _document.UndoStack.Push(new FormatChange(this, Current, format));
        Set(format);
    }

    private void Set(EditorFileFormat format)
    {
        Current = format;
        Changed?.Invoke(format);
    }

    private sealed class FormatChange(DocumentFormatHistory owner, EditorFileFormat before, EditorFileFormat after) : IUndoableOperation
    {
        public void Undo() => owner.Set(before);
        public void Redo() => owner.Set(after);
    }
}
