using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RustText.Demo.Platform;

internal static partial class NativePayloadBootstrap
{
    private const string ResourcePrefix = "RustText.Demo.NativePayload.";
    private static readonly string[] NativeFiles = ["libHarfBuzzSharp.dll", "libSkiaSharp.dll", "av_libglesv2.dll"];
    private static readonly object Gate = new();
    private static IntPtr[]? _moduleHandles;
    private static IntPtr _directoryCookie;

    internal static void EnsureLoaded()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Встроенные графические библиотеки RustText собраны для Windows x64.");
        lock (Gate)
        {
            if (_moduleHandles is not null) return;

            var assembly = typeof(NativePayloadBootstrap).Assembly;
            var embedded = ReadPayloadHashes(assembly);
            if (embedded is null)
            {
                if (RuntimeFeature.IsDynamicCodeSupported) return;
                throw new InvalidDataException("В исполняемый файл не встроены графические библиотеки RustText.");
            }

            var directory = MaterializePayloads(assembly, embedded);

            const uint loadLibrarySearchSystem32 = 0x00000800;
            const uint loadLibrarySearchUserDirs = 0x00000400;
            if (!SetDefaultDllDirectories(loadLibrarySearchSystem32 | loadLibrarySearchUserDirs))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "Не удалось включить безопасный поиск нативных библиотек RustText.");
            }

            _directoryCookie = AddDllDirectory(directory);
            if (_directoryCookie == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось добавить кеш библиотек RustText.");

            var handles = new List<IntPtr>(NativeFiles.Length);
            try
            {
                foreach (var name in NativeFiles)
                {
                    handles.Add(NativeLibrary.Load(Path.Combine(directory, name)));
                }

                _moduleHandles = handles.ToArray();
            }
            catch
            {
                foreach (var handle in handles) NativeLibrary.Free(handle);
                if (_directoryCookie != IntPtr.Zero)
                {
                    RemoveDllDirectory(_directoryCookie);
                    _directoryCookie = IntPtr.Zero;
                }
                throw;
            }
        }
    }

    private static Dictionary<string, byte[]>? ReadPayloadHashes(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + "sha256");
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        var expectedNames = new HashSet<string>(NativeFiles, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0 || line.Length != separator + 65)
            {
                throw new InvalidDataException("Недопустимая запись контрольной суммы встроенной библиотеки.");
            }

            var name = line[..separator];
            if (!expectedNames.Contains(name) || result.ContainsKey(name))
                throw new InvalidDataException($"Недопустимое имя встроенной библиотеки в контрольных суммах: {name}.");
            try
            {
                result.Add(name, Convert.FromHexString(line.AsSpan(separator + 1)));
            }
            catch (FormatException error)
            {
                throw new InvalidDataException($"Недопустимая контрольная сумма встроенной библиотеки {name}.", error);
            }
        }

        if (result.Count != NativeFiles.Length)
            throw new InvalidDataException("В списке контрольных сумм отсутствует встроенная библиотека.");

        return result;
    }

    private static string MaterializePayloads(Assembly assembly, Dictionary<string, byte[]> hashes)
    {
        var profileOverride = Environment.GetEnvironmentVariable("RUSTTEXT_PROFILE_DIR");
        string appRoot;
        if (!string.IsNullOrWhiteSpace(profileOverride))
        {
            if (!Path.IsPathFullyQualified(profileOverride))
                throw new IOException("RUSTTEXT_PROFILE_DIR должен содержать абсолютный путь.");
            appRoot = Path.GetFullPath(profileOverride);
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) throw new IOException("Папка локальных данных недоступна.");
            appRoot = Path.GetFullPath(Path.Combine(local, "RustText"));
        }

        Directory.CreateDirectory(appRoot);
        if ((File.GetAttributes(appRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Корневая папка RustText не должна быть ссылкой.");

        var root = Path.Combine(appRoot, "native");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка кеша библиотек RustText не должна быть ссылкой.");
        var version = ComputePayloadVersion(hashes);
        var stable = Path.Combine(root, version);
        if (IsComplete(stable, hashes)) return stable;

        // Repair into a stable immutable sibling so later launches reuse it while
        // another process may still have the damaged directory mapped.
        var repaired = Path.Combine(root, version + "-repaired");
        if (IsComplete(repaired, hashes)) return repaired;
        var target = Directory.Exists(stable) ? repaired : stable;
        if (Directory.Exists(target)) target = Path.Combine(root, $"{version}-{Guid.NewGuid():N}");

        var temporary = Path.Combine(root, $".{version}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var (name, expectedHash) in hashes)
            {
                var path = Path.Combine(temporary, name);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.WriteThrough))
                {
                    using var resource = assembly.GetManifestResourceStream(ResourcePrefix + name)
                        ?? throw new InvalidDataException($"Не найден встроенный ресурс {name}.");
                    resource.CopyTo(stream);
                    stream.Flush(flushToDisk: true);
                }
                if (!HashMatches(path, expectedHash)) throw new IOException($"Не удалось проверить кешированную библиотеку {name}.");
            }

            if (!Directory.Exists(target))
            {
                try
                {
                    Directory.Move(temporary, target);
                    return target;
                }
                catch (IOException) when (Directory.Exists(target) && IsComplete(target, hashes))
                {
                    Directory.Delete(temporary, recursive: true);
                    return target;
                }
            }

            // A damaged cache may still be in use by another process. Keep it intact and
            // use a fresh immutable directory for this process instead of replacing it.
            var isolated = Path.Combine(root, $"{version}-{Guid.NewGuid():N}");
            Directory.Move(temporary, isolated);
            return isolated;
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static bool IsComplete(string directory, Dictionary<string, byte[]> hashes)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var (name, expectedHash) in hashes)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || !HashMatches(path, expectedHash)) return false;
        }
        return true;
    }

    private static bool HashMatches(string path, byte[] expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            128 * 1024, FileOptions.SequentialScan);
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string ComputePayloadVersion(Dictionary<string, byte[]> hashes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in NativeFiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(hashes[name]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetDefaultDllDirectories", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "AddDllDirectory", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AddDllDirectory(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDllDirectory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDllDirectory(IntPtr cookie);
}
