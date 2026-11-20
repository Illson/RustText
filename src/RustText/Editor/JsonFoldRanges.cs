using AvaloniaEdit.Folding;

namespace RustText.Demo.Editor;

internal static class JsonFoldRanges
{
    public static List<NewFolding> Create(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var foldings = new List<NewFolding>();
        var openDelimiters = new List<(int Offset, char Character)>();
        var inString = false;
        var escaped = false;
        var lastLineBreak = -1;

        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = text[index];
            if (character is '\r' or '\n')
            {
                lastLineBreak = index;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                openDelimiters.Add((index, character));
                continue;
            }

            if (character is not ('}' or ']') || openDelimiters.Count == 0)
            {
                continue;
            }

            var opening = openDelimiters[^1];
            if (character != (opening.Character == '{' ? '}' : ']'))
            {
                openDelimiters.Clear();
                continue;
            }

            openDelimiters.RemoveAt(openDelimiters.Count - 1);
            if (lastLineBreak > opening.Offset)
            {
                foldings.Add(new NewFolding
                {
                    StartOffset = opening.Offset,
                    EndOffset = index + 1,
                    Name = opening.Character == '{' ? "{…}" : "[…]"
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        foldings.Sort(static (left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;
    }
}
