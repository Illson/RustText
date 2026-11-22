using System.Text.RegularExpressions;

namespace RustText.Demo.Editor;

internal readonly record struct TextMatch(int Index, int Length);

internal readonly struct TextMatcher
{
    public TextMatcher(SearchOptions options, Regex? expression = null)
    {
        if (options.Pattern.Length == 0) throw new ArgumentException("Введите текст для поиска.");
        if (!options.IsRegex && options.MatchCase && !options.WholeWord)
            Literal = options.Pattern;
        else
            Expression = expression ?? TextOperations.BuildRegex(options);
    }

    public string? Literal { get; }
    public Regex? Expression { get; }

    public Matches Enumerate(ReadOnlySpan<char> text, int start = 0) => new(text, start, Literal, Expression);

    internal ref struct Matches(ReadOnlySpan<char> text, int start, string? literal, Regex? expression)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _next = start;
        private Regex.ValueMatchEnumerator _regex = expression is null ? default : expression.EnumerateMatches(text, start);

        public TextMatch Current { get; private set; }
        public Matches GetEnumerator() => this;

        public bool MoveNext()
        {
            if (literal is null)
            {
                if (!_regex.MoveNext()) return false;
                Current = new(_regex.Current.Index, _regex.Current.Length);
                return true;
            }

            var relative = _text[_next..].IndexOf(literal.AsSpan(), StringComparison.Ordinal);
            if (relative < 0) return false;
            Current = new(_next + relative, literal.Length);
            _next = Current.Index + Current.Length;
            return true;
        }
    }
}
