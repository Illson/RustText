using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace RustText.Demo.Editor;

internal static class EditorClipboard
{
    internal static Action BindPaste(TextEditor editor, Func<bool> canPaste, Action<string> insert, Action<Func<Task>> run)
    {
        var queue = new SemaphoreSlim(1);
        var binding = new RoutedCommandBinding(ApplicationCommands.Paste,
            (_, e) => { e.Handled = true; run(PasteAsync); },
            (_, e) => { e.CanExecute = CanPaste(); e.Handled = true; });
        editor.TextArea.CommandBindings.Insert(0, binding);
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        return () =>
        {
            editor.TextArea.CommandBindings.Remove(binding);
            editor.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        };

        bool CanPaste() => !editor.IsReadOnly && editor.IsEnabled && canPaste();

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control) return;
            e.Handled = true;
            run(PasteAsync);
        }

        async Task PasteAsync()
        {
            if (!CanPaste() || TopLevel.GetTopLevel(editor)?.Clipboard is not { } clipboard) return;
            var document = editor.Document;
            await queue.WaitAsync();
            try
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text) && ReferenceEquals(document, editor.Document) && CanPaste()) insert(text);
            }
            finally { queue.Release(); }
        }
    }

    internal static void PasteText(TextEditor editor, string text)
    {
        var area = editor.TextArea;
        if (area.Selection is SimpleSelection { EnableVirtualSpace: false, SurroundingSegment: { } segment }
            && MatchesSelection(editor.Document, segment, text))
        {
            area.Caret.Offset = segment.EndOffset;
            area.ClearSelection();
            area.Caret.BringCaretToView();
            return;
        }

        area.PerformTextInput(text);
    }

    private static bool MatchesSelection(TextDocument document, ISegment segment, string text)
    {
        if (segment.Length != text.Length) return false;
        if (text.Length <= 256)
            return document.GetTextAsMemory(segment.Offset, segment.Length).Span.SequenceEqual(text);

        // A selection can cover the entire file; compare without making another full copy.
        using var reader = document.CreateReader(segment.Offset, segment.Length);
        var buffer = new char[1024];
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining.Length));
            if (read == 0 || !remaining[..read].SequenceEqual(buffer.AsSpan(0, read))) return false;
            remaining = remaining[read..];
        }
        return true;
    }

    internal static async Task CutLineAsync(TextEditor editor, Func<bool> canCut)
    {
        if (!canCut() || editor.IsReadOnly || !editor.TextArea.Selection.IsEmpty
            || TopLevel.GetTopLevel(editor)?.Clipboard is not { } clipboard) return;
        var document = editor.Document;
        var caretOffset = editor.CaretOffset;
        var line = document.GetLineByOffset(caretOffset);
        var length = line.TotalLength;
        if (length == 0)
        {
            return;
        }

        var lineText = document.GetText(line.Offset, length);
        var deleteStart = line.Offset;
        var deleteLength = length;
        if (line.DelimiterLength == 0 && line.PreviousLine is { } previousLine)
        {
            deleteStart = previousLine.EndOffset;
            deleteLength = line.EndOffset - deleteStart;
        }

        var documentChanged = false;
        EventHandler<DocumentChangeEventArgs> changed = (_, _) => documentChanged = true;
        document.Changed += changed;
        try
        {
            await clipboard.SetTextAsync(lineText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        }
        finally
        {
            document.Changed -= changed;
        }

        if (!canCut() || editor.IsReadOnly || !editor.IsEnabled
            || !ReferenceEquals(editor.Document, document) || editor.CaretOffset != caretOffset
            || !editor.TextArea.Selection.IsEmpty || documentChanged)
        {
            return;
        }

        document.Remove(deleteStart, deleteLength);
        editor.CaretOffset = Math.Min(deleteStart, document.TextLength);
        editor.TextArea.ClearSelection();
    }
}
