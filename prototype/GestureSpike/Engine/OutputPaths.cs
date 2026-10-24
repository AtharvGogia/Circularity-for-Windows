using System.IO;

namespace GestureSpike.Engine;

internal static class OutputPaths
{
    /// <summary>
    /// Picks "photo.png" (or "photo (resized).png" with a suffix), adding " (1)", " (2)"... if
    /// that's taken, next to the input. Creates it as an empty file so two jobs running at once
    /// can never pick the same name.
    /// </summary>
    public static string Reserve(string inputPath, string extension, string? suffix = null)
    {
        string dir = Path.GetDirectoryName(inputPath)!;
        string name = Path.GetFileNameWithoutExtension(inputPath);
        if (suffix != null)
            name += $" ({suffix})";

        for (int i = 0; ; i++)
        {
            string candidate = Path.Combine(dir, i == 0 ? $"{name}.{extension}" : $"{name} ({i}).{extension}");
            try
            {
                new FileStream(candidate, FileMode.CreateNew).Dispose();
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken; try the next number.
            }
        }
    }
}
