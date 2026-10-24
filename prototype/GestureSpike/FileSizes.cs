using System.Globalization;

namespace GestureSpike;

internal static class FileSizes
{
    public const long KB = 1024;
    public const long MB = 1024 * 1024;

    /// <summary>"820 KB", "2.4 MB".</summary>
    public static string Format(long bytes) => bytes switch
    {
        < KB => $"{bytes} bytes",
        < MB => $"{(bytes + KB / 2) / KB} KB",
        _ => (bytes / (double)MB).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
    };
}
