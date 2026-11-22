using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RustText.Demo.Editor;

// Match.Result expands the text. This companion records where each emitted line break came from.
internal sealed partial class ReplacementLineEndings
{
    [GeneratedRegex(@"\$\$|\$(?:[&`'+_]|[0-9]+|\{\w+\})", RegexOptions.CultureInvariant)]
    private static partial Regex ReplacementTokens();

    private readonly string _source;
    private readonly string _template;
    private readonly ImmutableList<LineEndingKind> _original;
    private readonly Action _checkOperation;
    private readonly List<int> _breakEnds = [];
    private readonly List<(Match Token, Func<Match, Range> Source)> _references = [];
    private bool _previousWasCr;
    public ImmutableList<LineEndingKind>.Builder Result { get; } = ImmutableList.CreateBuilder<LineEndingKind>();

    public ReplacementLineEndings(string source, ImmutableList<LineEndingKind> original, Regex? regex,
        string template, bool expandGroups, Action checkOperation)
    {
        _source = source;
        _original = original;
        _template = template;
        _checkOperation = checkOperation;

        for (var offset = 0; offset < source.Length;)
        {
            var next = source.AsSpan(offset).IndexOfAny('\r', '\n');
            if (next < 0) break;
            offset += next;
            if (source[offset] == '\r' && offset + 1 < source.Length && source[offset + 1] == '\n') offset++;
            _breakEnds.Add(offset++);
            if ((_breakEnds.Count & 0x3fff) == 0) checkOperation();
        }

        if (!expandGroups) return;
        foreach (Match token in ReplacementTokens().Matches(template))
        {
            // Consume $$ as one token so its second dollar cannot start a reference.
            if (token.Value == "$$") continue;
            Func<Match, Range>? range = token.Value switch
            {
                "$`" => m => 0..m.Index,
                "$'" => m => (m.Index + m.Length)..source.Length,
                "$_" => _ => 0..source.Length,
                "$&" => CaptureRange(0),
                "$+" => CaptureRange(regex!.GetGroupNumbers()[^1]),
                _ => NamedOrNumberedRange(regex!, token.Value)
            };
            if (range is not null) _references.Add((token, range));
        }
    }

    private static Func<Match, Range> CaptureRange(int number) => match =>
    {
        var group = match.Groups[number];
        return group.Index..(group.Index + group.Length);
    };

    private static Func<Match, Range>? NamedOrNumberedRange(Regex regex, string token)
    {
        var name = token[1] == '{' ? token[2..^1] : token[1..];
        var number = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : regex.GroupNumberFromName(name);
        return number >= 0 && regex.GroupNameFromNumber(number).Length != 0 ? CaptureRange(number) : null;
    }

    public void AppendSource(int start, int end)
    {
        if (start == end) return;
        var first = FirstBreakAt(start);
        var last = FirstBreakAt(end);
        // An output segment may split a CRLF pair or join two halves from different captures.
        if (_previousWasCr && _source[start] == '\n') first++;
        _previousWasCr = _source[end - 1] == '\r';
        if (_previousWasCr && end < _source.Length && _source[end] == '\n') last++;
        var available = Math.Min(last, _original.Count);
        if (available > first) Result.AddRange(_original.GetRange(first, available - first));
        if (last > Math.Max(first, available))
            Result.AddRange(Enumerable.Repeat(LineEndingKind.Lf, last - Math.Max(first, available)));
    }

    public void AppendReplacement(Match match, string inserted)
    {
        var first = FirstBreakAt(match.Index);
        var limit = FirstBreakAt(match.Index + match.Length);
        if (_references.Count == 0)
        {
            Append(inserted, first, limit);
            return;
        }

        var offset = 0;
        var initialCount = Result.Count;
        foreach (var (token, sourceRange) in _references)
        {
            // Literal breaks retain their ordinal style in the replaced span even next to a group.
            Append(_template.AsSpan(offset, token.Index - offset), first + (Result.Count - initialCount), limit);
            var range = sourceRange(match);
            AppendSource(range.Start.Value, range.End.Value);
            offset = token.Index + token.Length;
        }
        Append(_template.AsSpan(offset), first + (Result.Count - initialCount), limit);
    }

    public void AppendLiteralReplacement(int index, int length, string inserted) =>
        Append(inserted, FirstBreakAt(index), FirstBreakAt(index + length));

    private int FirstBreakAt(int offset)
    {
        var index = _breakEnds.BinarySearch(offset);
        return index >= 0 ? index : ~index;
    }

    private void Append(ReadOnlySpan<char> text, int first, int limit)
    {
        var marker = first;
        while (!text.IsEmpty)
        {
            var next = text.IndexOfAny('\r', '\n');
            if (next < 0) { _previousWasCr = false; return; }
            var ch = text[next];
            if (!(next == 0 && ch == '\n' && _previousWasCr))
                Result.Add(marker < limit && marker < _original.Count ? _original[marker] : LineEndingKind.Lf);
            _previousWasCr = ch == '\r';
            text = text[(next + 1)..];
            if (_previousWasCr && !text.IsEmpty && text[0] == '\n')
            {
                text = text[1..];
                _previousWasCr = false;
            }
            if ((++marker & 0x3fff) == 0) _checkOperation();
        }
    }
}
