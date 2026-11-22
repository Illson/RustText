using AvaloniaEdit.Document;

namespace RustText.Demo.Editor;

// The saved snapshot shares the document's text storage and does not retain its version chain.
internal sealed class DocumentSavePoint(ITextSource savedText, EditorFileFormat savedFormat, bool textIsCurrent)
{
    private int _unchangedPrefix = textIsCurrent ? savedText.TextLength : 0;
    private int _unchangedSuffix = textIsCurrent ? savedText.TextLength : 0;
    private bool _checkText = !textIsCurrent;
    private bool _sameText = textIsCurrent;
    private EditorFileFormat? _checkedFormat;
    private bool _sameFormat;

    public void OnTextChanging(int oldLength, DocumentChangeEventArgs change)
    {
        // Everything before and after the affected range still matches the saved snapshot.
        _unchangedPrefix = Math.Min(_unchangedPrefix, change.Offset);
        _unchangedSuffix = Math.Min(_unchangedSuffix, oldLength - change.Offset - change.RemovalLength);
        _checkText = true;
    }

    public bool Matches(TextDocument document, EditorFileFormat currentFormat)
    {
        if (_checkText)
        {
            _checkText = false;
            _sameText = document.TextLength == savedText.TextLength && MatchesChangedRange(document);
            if (_sameText) _unchangedPrefix = _unchangedSuffix = savedText.TextLength;
        }
        if (!_sameText) return false;

        if (!ReferenceEquals(_checkedFormat, currentFormat))
        {
            _checkedFormat = currentFormat;
            _sameFormat = currentFormat == savedFormat
                || currentFormat.Encoding == savedFormat.Encoding && currentFormat.Ending == savedFormat.Ending
                && currentFormat.MixedEndings is { } current && savedFormat.MixedEndings is { } saved
                && current.Count == saved.Count && current.SequenceEqual(saved);
        }
        return _sameFormat;
    }

    private bool MatchesChangedRange(TextDocument document)
    {
        var length = savedText.TextLength - _unchangedPrefix - _unchangedSuffix;
        if (length <= 256)
        {
            var expected = savedText.GetTextAsMemory(_unchangedPrefix, length).Span;
            var actual = document.GetTextAsMemory(_unchangedPrefix, length).Span;
            var matched = expected.CommonPrefixLength(actual);
            _unchangedPrefix += matched;
            return matched == length;
        }

        // Widely separated edits may leave a large range; don't materialize either full text.
        using var expectedReader = savedText.CreateReader(_unchangedPrefix, length);
        using var actualReader = document.CreateReader(_unchangedPrefix, length);
        const int chunkSize = 1024;
        var buffer = new char[2 * chunkSize];
        while (length > 0)
        {
            var count = Math.Min(length, chunkSize);
            if (expectedReader.ReadBlock(buffer, 0, count) != count || actualReader.ReadBlock(buffer, chunkSize, count) != count)
                return false;
            var matched = buffer.AsSpan(0, count).CommonPrefixLength(buffer.AsSpan(chunkSize, count));
            _unchangedPrefix += matched;
            if (matched != count) return false;
            length -= count;
        }
        return true;
    }
}
