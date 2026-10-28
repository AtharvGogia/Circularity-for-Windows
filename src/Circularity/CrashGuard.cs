using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Circularity;

/// <summary>
/// The safety net for errors nothing else caught. On the app's own thread the error is recorded and
/// Circularity carries on. Anywhere else .NET has to end the process, so the error is recorded,
/// the tray icon is taken down (or it would linger until hovered), and Circularity starts itself
/// again, unless it had only just started, so a crash at startup can't turn into a loop. Either way
/// the next notification, or the next start, offers to report it.
/// </summary>
internal static class CrashGuard
{
    /// <summary>A copy of every crash, newest last; only the last few are kept.</summary>
    public static readonly string Folder = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "crashes");

    /// <summary>Written when the process is about to end, and read (then deleted) by the next start.</summary>
    private static string PendingPath => Path.Combine(Folder, "pending.txt");

    /// <summary>Passed to the copy started after a crash, so it knows not to restart again straight away.</summary>
    public const string RestartedArgument = "--restarted-after-crash";

    /// <summary>A crash sooner than this after starting doesn't restart, so a broken start can't loop.</summary>
    internal static TimeSpan RestartAfterUptime { get; set; } = TimeSpan.FromSeconds(60);

    private const int KeepReports = 10;

    private static readonly DateTime Started = DateTime.Now;
    private static DateTime _lastNotified = DateTime.MinValue;

    /// <summary>
    /// Hooks the handlers up. <paramref name="recovered"/> runs on the app's thread after an error it
    /// survived, with the saved report, to tell the user; <paramref name="dying"/> runs (on any thread)
    /// just before the process ends, to tidy up the tray icon.
    /// </summary>
    public static void Install(Application app, Action<CrashReport> recovered, Action dying)
    {
        app.DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            var report = Save(args.Exception, fatal: false);
            // A fault that repeats (on every mouse move, say) would otherwise fill the screen with notifications.
            if (DateTime.Now - _lastNotified < TimeSpan.FromSeconds(30))
                return;
            _lastNotified = DateTime.Now;
            recovered(report);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString());
            if (!args.IsTerminating)
            {
                Save(exception, fatal: false);
                return;
            }
            var report = Save(exception, fatal: true);
            TryWrite(PendingPath, report.Path);
            try
            {
                dying();
            }
            catch
            {
                // Already going down; nothing more can be done.
            }
            if (DateTime.Now - Started >= RestartAfterUptime)
                Restart();
            else
                Log.Write("Crashed soon after starting, so not restarting.");
        };

        // A task whose error nobody looked at; .NET ignores these now, but they're worth a line in the log.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"Unobserved task error: {args.Exception}");
            args.SetObserved();
        };
    }

    /// <summary>The report left by a crash that ended the last run, if any, and forgets it.</summary>
    public static CrashReport? TakePending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            string path = File.ReadAllText(PendingPath).Trim();
            File.Delete(PendingPath);
            return File.Exists(path) ? CrashReport.Read(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records an error in the log and in a crash report of its own.</summary>
    internal static CrashReport Save(Exception exception, bool fatal)
    {
        Log.Write($"{(fatal ? "Fatal error" : "Unhandled error")}: {exception}");
        string time = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var text = new StringBuilder()
            .AppendLine($"Circularity {Diagnostics.AppVersion()} {(fatal ? "closed" : "recovered from an error")} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.")
            .AppendLine($"Windows: {Environment.OSVersion.VersionString}")
            .AppendLine($"Running for: {DateTime.Now - Started:hh\\:mm\\:ss}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();
        string path = Path.Combine(Folder, $"crash {time}{(fatal ? "" : " (recovered)")}.txt");
        TryWrite(path, text);
        Prune();
        return new CrashReport(path, exception.GetType().Name, exception.Message, text);
    }

    private static void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null)
                return;
            // The new copy waits for this one's single-instance lock to be released (see App.OnStartup).
            Process.Start(new ProcessStartInfo(exe, RestartedArgument) { UseShellExecute = false });
            Log.Write("Restarting after the crash.");
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't restart after the crash: {ex.Message}");
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles("crash *.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepReports))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old reports are only clutter.
        }
    }

    private static void TryWrite(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log line above still has it.
        }
    }
}

/// <summary>A saved crash: where it is, what it was, and the full text for a bug report.</summary>
internal sealed record CrashReport(string Path, string Kind, string Message, string Text)
{
    public static CrashReport Read(string path)
    {
        string text = File.ReadAllText(path);
        // The exception's first line, after the header, reads "System.SomeException: message".
        string first = text.Split('\n').Select(l => l.Trim()).Skip(4).FirstOrDefault(l => l.Length > 0) ?? "";
        int colon = first.IndexOf(": ", StringComparison.Ordinal);
        string kind = colon > 0 ? first[..colon].Split('.').Last() : "Error";
        string message = colon > 0 ? first[(colon + 2)..] : first;
        return new CrashReport(path, kind, message, text);
    }
}
