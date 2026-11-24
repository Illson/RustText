using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RustText.Demo.Editor;

// Match.Result expands the text. This companion records where each emitted line break came from.
internal sealed partial class ReplacementLineEndings
{
    [GeneratedRegex(@"\$\$|\$(?:[&`'+_]|[0-9]+|\{[\w\u200C\u200D]+\})", RegexOptions.CultureInvariant)]
    private static partial Regex ReplacementTokens();

    private const int BeforeMatch = -1;
    private const int AfterMatch = -2;
    private const int WholeInput = -3;
    private const int Unknown = -4;

    internal readonly record struct Reference(int Index, int Length, int GroupNumber)
    {
        public Range Source(Match match, int sourceLength)
        {
            if (GroupNumber == BeforeMatch) return 0..match.Index;
            if (GroupNumber == AfterMatch) return (match.Index + match.Length)..sourceLength;
            if (GroupNumber == WholeInput) return 0..sourceLength;
            var group = match.Groups[GroupNumber];
            return group.Index..(group.Index + group.Length);
        }
    }

    private readonly string _source;
    private readonly string _template;
    private readonly ImmutableList<LineEndingKind> _original;
    private readonly Action _checkOperation;
    private readonly List<int> _breakEnds = [];
    private readonly IReadOnlyList<Reference> _references;
    private bool _previousWasCr;
    public ImmutableList<LineEndingKind>.Builder Result { get; } = ImmutableList.CreateBuilder<LineEndingKind>();

    public ReplacementLineEndings(string source, ImmutableList<LineEndingKind> original, string template,
        IReadOnlyList<Reference> references, Action checkOperation)
    {
        _source = source;
        _original = original;
        _template = template;
        _references = references;
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
    }

    internal static (int LiteralLength, List<Reference> References) Analyze(Regex regex, string template, Action checkOperation)
    {
        var references = new List<Reference>();
        var literalLength = template.Length;
        int? lastGroup = null;
        foreach (var token in ReplacementTokens().EnumerateMatches(template))
        {
            checkOperation();
            var value = template.AsSpan(token.Index + 1, token.Length - 1);
            if (value[0] == '$')
            {
                literalLength--;
                continue;
            }
            var number = value[0] switch
            {
                '`' => BeforeMatch,
                '\'' => AfterMatch,
                '_' => WholeInput,
                '&' => 0,
                '+' => lastGroup ??= regex.GetGroupNumbers()[^1],
                _ => NamedOrNumberedGroup(regex, value)
            };
            if (number == Unknown) continue;
            references.Add(new(token.Index, token.Length, number));
            literalLength -= token.Length;
        }
        return (literalLength, references);
    }

    private static int NamedOrNumberedGroup(Regex regex, ReadOnlySpan<char> name)
    {
        if (name[0] == '{') name = name[1..^1];
        var number = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : regex.GroupNumberFromName(name.ToString());
        return number >= 0 && regex.GroupNameFromNumber(number).Length != 0 ? number : Unknown;
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
        foreach (var reference in _references)
        {
            // Literal breaks retain their ordinal style in the replaced span even next to a group.
            Append(_template.AsSpan(offset, reference.Index - offset), first + (Result.Count - initialCount), limit);
            var range = reference.Source(match, _source.Length);
            AppendSource(range.Start.Value, range.End.Value);
            offset = reference.Index + reference.Length;
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
