using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Circularity;

/// <summary>
/// Appends timestamped lines to %LOCALAPPDATA%\Circularity\log.txt. Lines are written by a background
/// thread, so a caller never waits on the disk: the mouse hook logs from its own thread, and Windows
/// silently removes a hook that's slow to answer. <see cref="Flush"/> waits for what's queued.
/// </summary>
internal static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Circularity", "log.txt");

    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static Thread? _writer;
    private static bool _writing;

    static Log()
    {
        // Whatever is still queued when the app closes is written before it goes.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Write(string message)
    {
        // Stamped now, not when it reaches the disk.
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
        lock (Gate)
        {
            Pending.Enqueue(line);
            if (_writer == null)
            {
                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Log writer", Priority = ThreadPriority.BelowNormal };
                _writer.Start();
            }
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>Waits (up to a couple of seconds) until every line so far is in the file.</summary>
    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        lock (Gate)
        {
            while ((Pending.Count > 0 || _writing) && DateTime.UtcNow < deadline)
                Monitor.Wait(Gate, 50);
        }
    }

    private static void WriteLoop()
    {
        var text = new StringBuilder();
        while (true)
        {
            lock (Gate)
            {
                _writing = false;
                Monitor.PulseAll(Gate); // Wakes Flush.
                while (Pending.Count == 0)
                    Monitor.Wait(Gate);
                while (Pending.Count > 0)
                    text.Append(Pending.Dequeue());
                _writing = true;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, text.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down, even when the folder is locked down.
            }
            text.Clear();
        }
    }
}
