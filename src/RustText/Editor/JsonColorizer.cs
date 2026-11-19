using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RustText.Demo.Editor;

internal enum JsonTokenKind { Key, String, Number, Literal, Punctuation }

internal readonly record struct JsonToken(int Start, int Length, JsonTokenKind Kind);

internal sealed class JsonColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush[] Palette =
    [
        new ImmutableSolidColorBrush(Color.Parse("#AEC9E1")),
        new ImmutableSolidColorBrush(Color.Parse("#BDD3AA")),
        FocusTheme.Warning,
        new ImmutableSolidColorBrush(Color.Parse("#CDB9E1")),
        FocusTheme.Text
    ];

    public bool Enabled { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled)
        {
            return;
        }

        var document = CurrentContext.Document;
        foreach (var token in Tokenize(document.GetText(line),
            () => NextNonWhitespace(document, line.EndOffset) == ':'))
        {
            ChangeLinePart(line.Offset + token.Start, line.Offset + token.Start + token.Length,
                element => element.TextRunProperties.SetForegroundBrush(Palette[(int)token.Kind]));
        }
    }

    internal static bool Applies(string? path, TextDocument document) => path is null
        ? NextNonWhitespace(document, 0) is '{' or '['
        : TextOperations.IsJsonPath(path);

    internal static IEnumerable<JsonToken> Tokenize(string line, Func<bool>? colonOnNextLine = null)
    {
        var index = 0;
        while (index < line.Length)
        {
            var start = index++;
            var character = line[start];
            if (character == '"')
            {
                yield return new JsonToken(start, 1, JsonTokenKind.Punctuation);
                var valueStart = index;
                while (index < line.Length && line[index] != '"')
                {
                    index += line[index] == '\\' && index + 1 < line.Length ? 2 : 1;
                }

                var closed = index < line.Length;
                var kind = closed && IsKey(line, index + 1, colonOnNextLine)
                    ? JsonTokenKind.Key : JsonTokenKind.String;
                if (index > valueStart)
                {
                    yield return new JsonToken(valueStart, index - valueStart, kind);
                }
                if (closed)
                {
                    yield return new JsonToken(index++, 1, JsonTokenKind.Punctuation);
                }
            }
            else if (character is '{' or '}' or '[' or ']' or ':' or ',')
            {
                yield return new JsonToken(start, 1, JsonTokenKind.Punctuation);
            }
            else if (char.IsAsciiDigit(character) || character == '-' && index < line.Length && char.IsAsciiDigit(line[index]))
            {
                while (index < line.Length && (char.IsAsciiDigit(line[index]) || line[index] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    index++;
                }
                yield return new JsonToken(start, index - start, JsonTokenKind.Number);
            }
            else if (char.IsAsciiLetter(character))
            {
                while (index < line.Length && (char.IsAsciiLetterOrDigit(line[index]) || line[index] == '_'))
                {
                    index++;
                }

                if (line.AsSpan(start, index - start) is "true" or "false" or "null")
                {
                    yield return new JsonToken(start, index - start, JsonTokenKind.Literal);
                }
            }
        }
    }

    private static bool IsKey(string line, int index, Func<bool>? colonOnNextLine)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        return index < line.Length ? line[index] == ':' : colonOnNextLine?.Invoke() == true;
    }

    private static char? NextNonWhitespace(TextDocument document, int start)
    {
        for (var index = start; index < document.TextLength; index++)
        {
            var character = document.GetCharAt(index);
            if (character != '\uFEFF' && !char.IsWhiteSpace(character))
            {
                return character;
            }
        }
        return null;
    }
}
