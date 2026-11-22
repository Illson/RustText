using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RustText.Demo;

internal static class EncryptedTabFile
{
    public const string Extension = ".rte";
    public const int MaxTextBytes = 32 * 1024 * 1024;
    public const int MaxHintBytes = 1024;
    // RUSTENC1: magic | int32 LE hint length | salt | nonce | UTF-8 hint | ciphertext | tag.
    // Version 1 fixes the KDF parameters; the whole prefix through the hint is authenticated.
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MagicSize = 8;
    private const int SaltOffset = MagicSize + sizeof(int);
    private const int NonceOffset = SaltOffset + SaltSize;
    private const int HeaderSize = NonceOffset + NonceSize;
    private const int MaxContainerBytes = HeaderSize + MaxHintBytes + MaxTextBytes + TagSize;
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Magic => "RUSTENC1"u8;

    public static bool HasExtension(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    public static void EnsurePlaintextTarget(string path)
    {
        if (HasExtension(path))
            throw new IOException("Расширение .rte предназначено для шифрования. Выберите другое имя или используйте «Инструменты → Шифрование».");
    }

    public static byte[] Encrypt(string text, string password, string hint)
    {
        if (password.Length == 0)
            throw new ArgumentException("Введите пароль для шифрования.");
        var textSize = Utf8.GetByteCount(text);
        var hintSize = Utf8.GetByteCount(hint);
        if (textSize > MaxTextBytes || hintSize > MaxHintBytes)
            throw new ArgumentException("Максимальный размер текста — 32 МиБ, подсказки — 1024 байта в UTF-8.");

        var cipherOffset = HeaderSize + hintSize;
        var container = new byte[cipherOffset + textSize + TagSize];
        Magic.CopyTo(container);
        BinaryPrimitives.WriteInt32LittleEndian(container.AsSpan(MagicSize), hintSize);
        var salt = container.AsSpan(SaltOffset, SaltSize);
        var nonce = container.AsSpan(NonceOffset, NonceSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        Utf8.GetBytes(hint, container.AsSpan(HeaderSize, hintSize));

        Span<byte> key = stackalloc byte[32];
        byte[] plain = Utf8.GetBytes(text);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, salt, key, Iterations, HashAlgorithmName.SHA256);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Encrypt(nonce, plain, container.AsSpan(cipherOffset, textSize),
                container.AsSpan(cipherOffset + textSize, TagSize), container.AsSpan(0, cipherOffset));
            return container;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static string ReadHint(ReadOnlySpan<byte> container)
    {
        var cipherOffset = ReadCipherOffset(container);
        try
        {
            return Utf8.GetString(container[HeaderSize..cipherOffset]);
        }
        catch (DecoderFallbackException error)
        {
            throw InvalidFile(error);
        }
    }

    public static string Decrypt(ReadOnlySpan<byte> container, string password)
    {
        var cipherOffset = ReadCipherOffset(container);
        if (password.Length == 0)
            throw new ArgumentException("Введите пароль для расшифровки.");
        var encrypted = container[cipherOffset..^TagSize];
        Span<byte> key = stackalloc byte[32];
        var plain = new byte[encrypted.Length];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, container.Slice(SaltOffset, SaltSize), key, Iterations, HashAlgorithmName.SHA256);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(container.Slice(NonceOffset, NonceSize), encrypted, container[^TagSize..], plain, container[..cipherOffset]);
            return Utf8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException error)
        {
            throw new InvalidDataException("Неверный пароль или зашифрованный файл повреждён.", error);
        }
        catch (DecoderFallbackException error)
        {
            throw InvalidFile(error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static async Task<byte[]> ReadAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        if (file.Length is < HeaderSize + TagSize or > MaxContainerBytes) throw InvalidFile();
        var header = new byte[HeaderSize];
        await file.ReadExactlyAsync(header, token);
        ReadCipherOffset(header, (int)file.Length);
        var container = new byte[(int)file.Length];
        header.CopyTo(container, 0);
        await file.ReadExactlyAsync(container.AsMemory(HeaderSize), token);
        return container;
    }

    public static async Task WriteNewAsync(string path, byte[] container, CancellationToken token)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".rusttext-encrypted-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await file.WriteAsync(container, token);
                await file.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static int ReadCipherOffset(ReadOnlySpan<byte> container) => ReadCipherOffset(container, container.Length);

    private static int ReadCipherOffset(ReadOnlySpan<byte> header, int size)
    {
        if (size is < HeaderSize + TagSize or > MaxContainerBytes || !header[..MagicSize].SequenceEqual(Magic))
            throw InvalidFile();
        var hintSize = BinaryPrimitives.ReadInt32LittleEndian(header[MagicSize..]);
        var textSize = size - HeaderSize - TagSize;
        if (hintSize is < 0 or > MaxHintBytes || hintSize > textSize || textSize - hintSize > MaxTextBytes)
            throw InvalidFile();
        return HeaderSize + hintSize;
    }

    private static InvalidDataException InvalidFile(Exception? inner = null) =>
        new("Это не зашифрованный файл RustText версии 1 либо он повреждён.", inner);
}
