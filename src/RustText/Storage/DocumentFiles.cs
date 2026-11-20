using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace RustText.Demo;

public enum TextEncodingKind { Utf8, Utf8Bom, Utf16LittleEndian, Utf16BigEndian, Windows1251 }
public enum LineEndingKind : byte { Lf, CrLf, Cr, Mixed }

public sealed record FileStamp(long Length, DateTime LastWriteUtc, string Sha256)
{
    public static async Task<FileStamp> CaptureAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CaptureAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<FileStamp> CaptureAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var length = stream.Length;
        var modified = File.GetLastWriteTimeUtc(stream.Name);
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new(length, modified, hash);
    }
}

public sealed record LoadedDocument(string Text, TextEncodingKind Encoding, LineEndingKind LineEnding, ImmutableArray<LineEndingKind> OriginalLineEndings, FileStamp Stamp);

public static class DocumentFiles
{
    public const long MaximumDocumentBytes = 50L * 1024 * 1024;
    static DocumentFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string DecodeText(byte[] bytes, TextEncodingKind encoding) => Decode(bytes, encoding).Text;

    internal static (string Text, TextEncodingKind Encoding, LineEndingKind Ending, ImmutableArray<LineEndingKind> Endings)
        DecodeSnapshot(byte[] bytes, TextEncodingKind encoding)
    {
        var (decoded, encodingUsed) = Decode(bytes, encoding);
        var (text, endings, style) = NormalizeNewlines(decoded);
        return (text, encodingUsed, style, endings);
    }

    public static Task<FileStamp> GetStampAsync(string path, CancellationToken cancellationToken = default) =>
        FileStamp.CaptureAsync(Path.GetFullPath(path), cancellationToken);

    public static async Task<LoadedDocument> ReadAsync(string path, TextEncodingKind? encoding = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumDocumentBytes) throw new IOException("Файл больше лимита редактора (50 МиБ).");
        var before = await FileStamp.CaptureAsync(stream, cancellationToken).ConfigureAwait(false);
        if (before.Length > MaximumDocumentBytes) throw new IOException("Файл больше лимита редактора (50 МиБ).");
        stream.Position = 0;
        var bytes = new byte[checked((int)before.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var after = await FileStamp.CaptureAsync(stream, cancellationToken).ConfigureAwait(false);
        if (before != after) throw new IOException("Файл изменился во время чтения.");

        var (decoded, detected) = Decode(bytes, encoding);
        if (decoded.Contains('\0')) throw new InvalidDataException("Файл содержит нулевые символы.");
        var (normalized, endings, style) = NormalizeNewlines(decoded);
        return new(normalized, detected, style, endings, before);
    }

    public static async Task<FileStamp> SaveAsync(string path, string text, TextEncodingKind encoding, LineEndingKind lineEnding, FileStamp? expectedStamp, ImmutableArray<LineEndingKind> originalLineEndings = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var fullPath = Path.GetFullPath(path);
        var bytes = Encode(text, encoding, lineEnding, originalLineEndings);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Не удалось определить папку файла.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.pending");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = new FileStamp(bytes.LongLength, File.GetLastWriteTimeUtc(temporary),
                Convert.ToHexString(SHA256.HashData(bytes)));
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedStamp is null)
            {
                File.Move(temporary, fullPath, overwrite: false);
            }
            else
            {
                // Lock the directory entry across cooperating writers, including other profiles
                // and network clients. The original handle alone permits another atomic rename.
                var nameHash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(fullPath).ToUpperInvariant()));
                var lockPath = Path.Combine(directory, $".rusttext-save-{Convert.ToHexString(nameHash)}.lock");
                using var publishLock = AcquireSaveLock(lockPath);
                await using var guard = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                var current = await FileStamp.CaptureAsync(guard, cancellationToken).ConfigureAwait(false);
                if (current != expectedStamp)
                    throw new IOException("Файл изменился на диске. Сохранение отменено, чтобы не затереть внешние изменения.");
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporary, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            return stamp;
        }
        finally
        {
            try { File.Delete(temporary); } catch (FileNotFoundException) { }
        }
    }

    private static FileStream AcquireSaveLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 80 or 183)
        {
            throw new IOException("Файл занят другой операцией сохранения. Повторите сохранение после её завершения.", ex);
        }
    }

    internal static byte[] Encode(string text, TextEncodingKind encoding, LineEndingKind lineEnding, ImmutableArray<LineEndingKind> originalEndings = default)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length + Math.Min(normalized.Length, 1024));
        var map = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c != '\n') { builder.Append(c); continue; }
            var ending = !originalEndings.IsDefault && map < originalEndings.Length ? originalEndings[map] : lineEnding == LineEndingKind.Mixed ? LineEndingKind.Lf : lineEnding;
            map++;
            builder.Append(ending switch { LineEndingKind.CrLf => "\r\n", LineEndingKind.Cr => "\r", _ => "\n" });
        }
        var textToWrite = builder.ToString();
        var selected = GetEncoding(encoding);
        var preamble = encoding switch
        {
            TextEncodingKind.Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF },
            TextEncodingKind.Utf16LittleEndian => new byte[] { 0xFF, 0xFE },
            TextEncodingKind.Utf16BigEndian => new byte[] { 0xFE, 0xFF },
            _ => []
        };
        var body = selected.GetBytes(textToWrite);
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static (string Text, TextEncodingKind Encoding) Decode(byte[] bytes, TextEncodingKind? requested)
    {
        var kind = requested ?? EncodingFromPreamble(bytes);
        var skip = 0;
        if (kind == TextEncodingKind.Utf8Bom && bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        if (kind == TextEncodingKind.Utf16LittleEndian && bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) skip = 2;
        if (kind == TextEncodingKind.Utf16BigEndian && bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) skip = 2;
        try { return (GetEncoding(kind).GetString(bytes, skip, bytes.Length - skip), kind); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException($"Файл не соответствует {kind}.", ex); }
    }

    private static TextEncodingKind EncodingFromPreamble(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xFE, ..] => TextEncodingKind.Utf16LittleEndian,
        [0xFE, 0xFF, ..] => TextEncodingKind.Utf16BigEndian,
        [0xEF, 0xBB, 0xBF, ..] => TextEncodingKind.Utf8Bom,
        _ => TextEncodingKind.Utf8
    };

    internal static TextEncodingKind DetectEncoding(byte[] bytes)
    {
        var kind = EncodingFromPreamble(bytes);
        _ = GetEncoding(kind).GetCharCount(bytes);
        return kind;
    }

    internal static Encoding GetEncoding(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom => new UTF8Encoding(false, true),
        TextEncodingKind.Utf16LittleEndian => new UnicodeEncoding(false, false, true),
        TextEncodingKind.Utf16BigEndian => new UnicodeEncoding(true, false, true),
        TextEncodingKind.Windows1251 => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static (string Text, ImmutableArray<LineEndingKind> Endings, LineEndingKind Style) NormalizeNewlines(string text)
    {
        var builder = new StringBuilder(text.Length);
        var endings = ImmutableArray.CreateBuilder<LineEndingKind>();
        var first = (LineEndingKind?)null;
        var mixed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                var kind = i + 1 < text.Length && text[i + 1] == '\n' ? LineEndingKind.CrLf : LineEndingKind.Cr;
                if (kind == LineEndingKind.CrLf) i++;
                builder.Append('\n'); endings.Add(kind);
                if (first is null) first = kind; else if (first != kind) mixed = true;
            }
            else if (c == '\n')
            {
                builder.Append('\n'); endings.Add(LineEndingKind.Lf);
                if (first is null) first = LineEndingKind.Lf; else if (first != LineEndingKind.Lf) mixed = true;
            }
            else builder.Append(c);
        }
        return (builder.ToString(), endings.ToImmutable(), mixed ? LineEndingKind.Mixed : first ?? LineEndingKind.Lf);
    }
}
