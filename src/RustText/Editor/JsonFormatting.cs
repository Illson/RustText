using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RustText.Demo.Editor;

internal static class JsonFormatting
{
    private const int MaxFastInput = 2 * 1024 * 1024;
    private const int MaxFastOutput = 4 * 1024 * 1024;
    private static readonly byte[] Newline = Encoding.UTF8.GetBytes(Environment.NewLine);
    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 128 };

    internal static void Validate(ReadOnlySpan<char> input, int byteCount, CancellationToken token)
    {
        if (byteCount < 0) byteCount = Encoding.UTF8.GetByteCount(input);
        var rented = byteCount is > 512 and <= MaxFastOutput ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        Span<byte> utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : rented ?? GC.AllocateUninitializedArray<byte>(byteCount);
        try
        {
            var length = Encoding.UTF8.GetBytes(input, utf8);
            var reader = new Utf8JsonReader(utf8[..length], ReaderOptions);
            var tokens = 0;
            while (reader.Read())
                if ((tokens++ & 255) == 0) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Use validated token spans directly as long as the writer would not
    // re-encode a string. Escapes or large outputs take the full writer path.
    internal static string? TryFormat(ReadOnlySpan<char> input, int byteCount, CancellationToken token)
    {
        if (byteCount > MaxFastInput) return null;
        var utf8 = ArrayPool<byte>.Shared.Rent(byteCount);
        byte[]? output = null;
        try
        {
            output = ArrayPool<byte>.Shared.Rent(Math.Min(MaxFastOutput, byteCount + byteCount / 2 + 256));
            var length = Encoding.UTF8.GetBytes(input, utf8);
            var reader = new Utf8JsonReader(utf8.AsSpan(0, length), ReaderOptions);
            var previous = JsonTokenType.None;
            var written = 0;
            var tokens = 0;

            while (reader.Read())
            {
                if ((tokens++ & 255) == 0) token.ThrowIfCancellationRequested();
                var current = reader.TokenType;
                var value = reader.ValueSpan;
                var quoted = current is JsonTokenType.String or JsonTokenType.PropertyName;
                if (quoted && (reader.ValueIsEscaped || JavaScriptEncoder.UnsafeRelaxedJsonEscaping.FindFirstCharacterToEncodeUtf8(value) >= 0))
                    return null;

                var punctuation = current switch
                {
                    JsonTokenType.StartObject => (byte)'{',
                    JsonTokenType.StartArray => (byte)'[',
                    JsonTokenType.EndObject => (byte)'}',
                    JsonTokenType.EndArray => (byte)']',
                    _ => (byte)0
                };
                var closes = current is JsonTokenType.EndObject or JsonTokenType.EndArray;
                var afterOpen = previous is JsonTokenType.StartObject or JsonTokenType.StartArray;
                var indent = previous is not (JsonTokenType.None or JsonTokenType.PropertyName) && (!closes || !afterOpen);
                var comma = indent && !closes && !afterOpen;
                var property = current == JsonTokenType.PropertyName;
                var required = written + (comma ? 1 : 0)
                    + (indent ? Newline.Length + reader.CurrentDepth * 2 : 0)
                    + (punctuation != 0 ? 1 : value.Length + (quoted ? 2 : 0) + (property ? 2 : 0));
                if (required > MaxFastOutput) return null;
                if (required > output.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Min(MaxFastOutput, Math.Max(required, output.Length * 2)));
                    output.AsSpan(0, written).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(output);
                    output = grown;
                }

                if (comma) output[written++] = (byte)',';
                if (indent)
                {
                    Newline.CopyTo(output.AsSpan(written));
                    written += Newline.Length;
                    var spaces = reader.CurrentDepth * 2;
                    output.AsSpan(written, spaces).Fill((byte)' ');
                    written += spaces;
                }
                if (punctuation != 0)
                {
                    output[written++] = punctuation;
                }
                else
                {
                    if (quoted) output[written++] = (byte)'"';
                    value.CopyTo(output.AsSpan(written));
                    written += value.Length;
                    if (quoted) output[written++] = (byte)'"';
                    if (property)
                    {
                        output[written++] = (byte)':';
                        output[written++] = (byte)' ';
                    }
                }
                previous = current;
            }

            token.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetString(output.AsSpan(0, written));
        }
        finally
        {
            if (output is not null) ArrayPool<byte>.Shared.Return(output);
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }
}
