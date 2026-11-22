using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

public sealed class TextOperationTests
{
    [Fact]
    public void OrdinalEditorMatchesAreNonoverlappingAndWrapFromTheSameOffsets()
    {
        const string text = "ababa\naba ABa";
        var matcher = new TextMatcher(new("aba", false, true, false));
        var result = TextOperations.Find(text, matcher, 2, default, 2);

        Assert.Equal([0, 6], result.Hits.Select(hit => hit.Offset));
        Assert.Equal([1, 2], result.Hits.Select(hit => hit.Line));
        Assert.Equal("ababa", result.Hits[0].Preview);
        Assert.Equal(2, result.Count);
        Assert.Equal(new TextMatch(2, 3), TextOperations.FindNext(text, matcher, 1, false));
        Assert.Equal(new TextMatch(0, 3), TextOperations.FindNext(text, matcher, 3, true));
        Assert.Equal(new TextMatch(6, 3), TextOperations.FindNext(text, matcher, 0, true));

        Assert.Equal(new ReplaceAllResult("$&ba\n$& ABa", 2), TextOperations.ReplaceAll(text, matcher, "$&", false));
        var missing = new TextMatcher(new("none", false, true, false));
        Assert.Same(text, TextOperations.ReplaceAll(text, missing, "", false).Text);
        Assert.Throws<OperationCanceledException>(() => TextOperations.ReplaceAll(text, missing, "", false, new(true)));
    }

    [Theory]
    [InlineData(" { \"a\" : [ -0, 1e+05, 0.00100, {}, [], null ], \"a\": \"текст <>\" } ")]
    [InlineData("[true, false, 3, {\"items\":[{\"a\": \"x\"}]}]")]
    [InlineData("\"plain string\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\uFEFF {\"escaped\\u002Fproperty\":\"quote\\\" and \\u0416\", \"sep\":\"\u2028\"}")]
    public void JsonFormattingMatchesSystemTextJsonWriter(string text)
    {
        using var input = JsonDocument.Parse(text.TrimStart('\uFEFF'));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions
        {
            Indented = true,
            MaxDepth = 128,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            input.WriteTo(writer);
        }
        Assert.Equal(Encoding.UTF8.GetString(bytes.ToArray()), TextOperations.FormatJson(text, "data.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF  \n")]
    [InlineData("[] true")]
    [InlineData("{\"а\": [2,]}")]
    public void JsonValidatorAndFormatterBothRejectInvalidInput(string text)
    {
        Assert.NotNull(TextOperations.ValidateJson(text, "data.json"));
        Assert.ThrowsAny<JsonException>(() => TextOperations.FormatJson(text, "data.json"));
    }

    [Fact]
    public void JsonRejectsUnpairedUtf16InsteadOfSilentlyReplacingIt()
    {
        const string input = """{"value": "a"}""";
        var invalid = input.Replace('a', '\uD800');
        Assert.ThrowsAny<ArgumentException>(() => TextOperations.ValidateJson(invalid, "data.json"));
        Assert.ThrowsAny<ArgumentException>(() => TextOperations.FormatJson(invalid, "data.json"));
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8)]
    [InlineData(TextEncodingKind.Utf8Bom)]
    [InlineData(TextEncodingKind.Utf16LittleEndian)]
    [InlineData(TextEncodingKind.Utf16BigEndian)]
    [InlineData(TextEncodingKind.Windows1251)]
    public async Task ReadSnapshotPreservesExactBytesStampAndMixedEndings(TextEncodingKind kind)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "mixed.txt");
        var bytes = DocumentFiles.Encode("ЁЖ\nстрока\n\nхвост\n", kind, LineEndingKind.Mixed,
            [LineEndingKind.CrLf, LineEndingKind.Cr, LineEndingKind.Lf, LineEndingKind.CrLf]);
        await File.WriteAllBytesAsync(path, bytes);

        var loaded = await DocumentFiles.ReadAsync(path, kind == TextEncodingKind.Windows1251 ? kind : null);
        Assert.Equal("ЁЖ\nстрока\n\nхвост\n", loaded.Text);
        Assert.Equal(kind, loaded.Encoding);
        Assert.Equal(LineEndingKind.Mixed, loaded.LineEnding);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), loaded.Stamp.Sha256);
        Assert.Equal(bytes.LongLength, loaded.Stamp.Length);
        Assert.Equal(loaded.Stamp, await DocumentFiles.GetStampAsync(path));
        Assert.Equal(bytes, DocumentFiles.Encode(loaded.Text, loaded.Encoding, loaded.LineEnding, loaded.OriginalLineEndings));
    }
}
