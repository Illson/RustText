using System.Text;
using System.Text.Json.Serialization;

namespace RustText.Demo.Platform;

public sealed record LaunchRequest(
    string WorkingDirectory,
    string[] Paths,
    bool Activate = true)
{
    [JsonIgnore]
    public IReadOnlyList<string> AbsolutePaths => Paths
        .Select(path => Path.IsPathRooted(path) ? path : Path.GetFullPath(path, WorkingDirectory))
        .ToArray();

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory) || !Path.IsPathFullyQualified(WorkingDirectory) || Paths is null)
        {
            throw new InvalidDataException("The launch request has an invalid working directory.");
        }

        if (Paths.Length > 512)
        {
            throw new InvalidDataException("The launch request contains too many paths.");
        }

        if (Encoding.UTF8.GetByteCount(WorkingDirectory) > 32 * 1024 ||
            Paths.Any(path => string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 32 * 1024))
        {
            throw new InvalidDataException("A launch path exceeds the size limit.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LaunchRequest))]
internal partial class LaunchRequestJsonContext : JsonSerializerContext;
