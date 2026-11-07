using System;
using System.Collections.Concurrent;
using System.IO;

namespace Circularity.Engine;

internal static class OutputPaths
{
    /// <summary>
    /// Where results go: a folder the user chose in Settings, or null for next to each original.
    /// Set by the app whenever settings change.
    /// </summary>
    public static string? SaveTo { get; set; }

    // Every file and folder results were written to since Circularity started, so a watched folder
    // never picks up Circularity's own results and converts them again.
    private static readonly ConcurrentDictionary<string, byte> Produced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a result Circularity wrote (or is writing), or anything inside a folder of results.</summary>
    public static bool IsProduced(string path)
    {
        string full = Path.GetFullPath(path);
        if (Produced.ContainsKey(full))
            return true;
        for (string? dir = Path.GetDirectoryName(full); dir != null; dir = Path.GetDirectoryName(dir))
            if (Produced.ContainsKey(dir))
                return true;
        return false;
    }

    private static string Remember(string path)
    {
        Produced[Path.GetFullPath(path)] = 0;
        return path;
    }

    /// <summary>
    /// Picks "photo.png" (or "photo (resized).png" with a suffix), adding " (1)", " (2)"... if
    /// that's taken, in the folder results go to. Creates it as an empty file so two jobs running at
    /// once can never pick the same name.
    /// </summary>
    public static string Reserve(string inputPath, string extension, string? suffix = null)
    {
        string dir = FolderFor(inputPath);
        string name = Path.GetFileNameWithoutExtension(inputPath);
        if (suffix != null)
            name += $" ({suffix})";

        for (int i = 0; ; i++)
        {
            string candidate = Path.Combine(dir, i == 0 ? $"{name}.{extension}" : $"{name} ({i}).{extension}");
            try
            {
                // Remembered before it exists, so a watcher that sees it appear already knows it.
                Remember(candidate);
                new FileStream(candidate, FileMode.CreateNew).Dispose();
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken (by someone else's file); try the next number.
                Produced.TryRemove(Path.GetFullPath(candidate), out _);
            }
            catch
            {
                // Not written (no permission, say): it isn't a result, so a watcher mustn't skip it.
                Produced.TryRemove(Path.GetFullPath(candidate), out _);
                throw;
            }
        }
    }

    /// <summary>Like <see cref="Reserve"/>, but creates a folder: "report (pages)", "report (pages) (1)"...</summary>
    public static string ReserveFolder(string inputPath, string suffix)
    {
        string dir = FolderFor(inputPath);
        string name = $"{Path.GetFileNameWithoutExtension(inputPath)} ({suffix})";
        for (int i = 0; ; i++)
        {
            string candidate = Path.Combine(dir, i == 0 ? name : $"{name} ({i})");
            if (Directory.Exists(candidate) || File.Exists(candidate))
                continue;
            Remember(candidate);
            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch
            {
                Produced.TryRemove(Path.GetFullPath(candidate), out _);
                throw;
            }
            return candidate;
        }
    }

    /// <summary>
    /// The chosen folder if there is one and it can be written to; otherwise, the original's own
    /// folder, so a missing folder (an unplugged drive, say) never loses a result.
    /// </summary>
    public static string FolderFor(string inputPath)
    {
        string beside = Path.GetDirectoryName(inputPath)!;
        string? chosen = SaveTo;
        if (string.IsNullOrWhiteSpace(chosen))
            return beside;
        try
        {
            Directory.CreateDirectory(chosen);
            // Proves it can be written to before anything is converted into it.
            string probe = Path.Combine(chosen, $".circularity-{Guid.NewGuid():N}.tmp");
            new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose).Dispose();
            return chosen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Write($"Can't save to {chosen} ({ex.Message}); saving next to the original instead.");
            return beside;
        }
    }
}
