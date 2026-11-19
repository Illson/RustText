using System.Globalization;

namespace RustText.Demo;

internal static class LineNumbers
{
    internal static NumberFormatInfo FormatInfo { get; } = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalDigits = 0
    };

    public static string Format(long value) => value.ToString("N0", FormatInfo);
}
