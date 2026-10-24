using System.IO;

namespace GestureSpike.Engine;

internal static class OutputPaths
{
    /// <summary>
    /// Picks "photo.png", or "photo (1).png" if that's taken, next to the input, and creates it
    /// as an empty file so two jobs running at once can never pick the same name.
    /// </summary>
    public static string Reserve(string inputPath, string target)
    {
        string dir = Path.GetDirectoryName(inputPath)!;
        string name = Path.GetFileNameWithoutExtension(inputPath);

        for (int i = 0; ; i++)
        {
            string candidate = Path.Combine(dir, i == 0 ? $"{name}.{target}" : $"{name} ({i}).{target}");
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
