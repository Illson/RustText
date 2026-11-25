using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RustText.Demo.Editor;

internal sealed class LineGenerator(string template, long rows)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly Regex Placeholders = new(@"\{(int_from_[^{}]*|guid|rand_string(?:_[^{}]*)?)\}",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(400));

    private readonly Part[] _parts = Parse(template, rows);

    public async Task<string> GenerateTextAsync(IProgress<double>? progress, CancellationToken token)
    {
        // System.String.MaxLength in the Windows x64 runtime, measured in UTF-16 code units.
        const int maxStringLength = 0x3FFFFFDF;
        Int128 length = rows - 1;
        foreach (var part in _parts)
        {
            if (length > maxStringLength) break;
            length += part.Kind switch
            {
                PartKind.Integer => IntegerCharacters(part.Number),
                PartKind.Guid => (Int128)36 * rows,
                PartKind.Random => (Int128)part.Number * rows,
                _ => (Int128)part.Text.Length * rows
            };
        }
        if (length > maxStringLength)
            throw new InvalidDataException("Результат не помещается в одну строку .NET. Выберите «В файл…».");
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        await GenerateAsync(writer, progress, token);
        return writer.ToString();
    }

    private Int128 IntegerCharacters(Int128 first)
    {
        var end = first + rows;
        var characters = rows + Int128.Clamp(-first, 0, rows); // One digit per row and a sign for negatives.
        for (Int128 place = 10; place <= Int128.Max(-first, end - 1); place *= 10)
            characters += Int128.Clamp(end - place, 0, rows) + Int128.Clamp(1 - place - first, 0, rows);
        return characters;
    }

    public async Task GenerateFileAsync(string path, IProgress<double>? progress, CancellationToken token)
    {
        var target = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".rusttext-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
                await GenerateAsync(writer, progress, token);
                await writer.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async Task GenerateAsync(TextWriter writer, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = new char[4096];
        for (long row = 0; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            if (row != 0)
            {
                await writer.WriteAsync('\n');
            }

            foreach (var part in _parts)
            {
                switch (part.Kind)
                {
                    case PartKind.Integer:
                        await writer.WriteAsync(checked(part.Number + row).ToString(CultureInfo.InvariantCulture));
                        break;
                    case PartKind.Guid:
                        await writer.WriteAsync(Guid.NewGuid().ToString());
                        break;
                    case PartKind.Random:
                        for (var remaining = part.Number; remaining > 0; remaining -= buffer.Length)
                        {
                            token.ThrowIfCancellationRequested();
                            var count = (int)Math.Min(remaining, buffer.Length);
                            for (var i = 0; i < count; i++)
                            {
                                buffer[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
                            }

                            await writer.WriteAsync(buffer.AsMemory(0, count), token);
                        }

                        break;
                    default:
                        await writer.WriteAsync(part.Text);
                        break;
                }
            }

            if ((row & 1023) == 0)
            {
                progress?.Report((double)row / rows);
            }
        }

        progress?.Report(1);
    }

    private static Part[] Parse(string template, long count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Укажите количество строк больше нуля.");
        }

        var parts = new List<Part>();
        var offset = 0;
        foreach (Match match in Placeholders.Matches(template))
        {
            AddLiteral(parts, template[offset..match.Index]);
            var token = match.Groups[1].Value;
            if (token == "guid")
            {
                parts.Add(new Part("", PartKind.Guid, 0));
            }
            else if (token.StartsWith("int_from_", StringComparison.Ordinal))
            {
                if (!long.TryParse(token.AsSpan(9), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var start))
                {
                    throw new ArgumentException("В {int_from_N} укажите целое начальное число.");
                }

                _ = checked(start + (count - 1));
                parts.Add(new Part("", PartKind.Integer, start));
            }
            else
            {
                var length = 12L;
                if (token != "rand_string" && !long.TryParse(token.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out length))
                {
                    throw new ArgumentException("В {rand_string_N} укажите неотрицательную целую длину.");
                }

                parts.Add(new Part("", PartKind.Random, length));
            }

            offset = match.Index + match.Length;
        }

        AddLiteral(parts, template[offset..]);
        return parts.ToArray();
    }

    private static void AddLiteral(List<Part> parts, string text)
    {
        if (text.Length != 0)
        {
            parts.Add(new Part(text, PartKind.Text, 0));
        }
    }

    private readonly record struct Part(string Text, PartKind Kind, long Number);
    private enum PartKind { Text, Integer, Guid, Random }
}
