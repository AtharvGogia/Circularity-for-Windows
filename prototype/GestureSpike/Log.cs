using System;
using System.IO;

namespace GestureSpike;

/// <summary>Appends timestamped lines to %LOCALAPPDATA%\GestureSpike\log.txt.</summary>
internal static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestureSpike", "log.txt");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app (or the mouse hook) down.
            }
        }
    }
}
