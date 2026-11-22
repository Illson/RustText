namespace RustText.Demo.Editor
{
    // The benchmark uses TextOperations.Find without generating Avalonia editor carets.
    internal readonly record struct CaretRange(int Anchor, int Caret);
}

namespace RustText.Demo.Logs
{
    // Only referenced by LooksLikeJson, which is not a measured operation.
    internal static class LogLevels
    {
        public static bool IsLogPath(string? path) =>
            string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
    }
}
