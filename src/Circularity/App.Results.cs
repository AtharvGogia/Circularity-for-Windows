using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// After a batch: the notification, clipboard, undo and freeing memory.
public partial class App
{
    private void ReportBatch(ConversionBatch batch)
    {
        UpdateTrayStatus();
        ScheduleMemoryRelease();

        var jobs = batch.Jobs;
        var succeeded = jobs.Where(j => j.State == JobState.Succeeded).ToList();
        var failed = jobs.Where(j => j.State == JobState.Failed).ToList();
        int cancelled = jobs.Count(j => j.State == JobState.Cancelled);

        foreach (var job in jobs)
        {
            string what = job.Operation.Describe();
            Log.Write(job.State switch
            {
                JobState.Succeeded => $"{what}: {job.InputPath} -> {job.OutputPath}",
                JobState.Failed => $"{what} failed for {job.InputPath}: {job.Error}",
                _ => $"{what} cancelled for {job.InputPath}",
            });
        }

        // The newest results are what "Undo last conversion" removes.
        if (succeeded.Count > 0)
        {
            _undo.Remember(succeeded.Select(j => j.OutputPath!));
            UpdateUndoItem();
        }

        if (failed.Count > 0)
        {
            string title = failed.Count == jobs.Count ? "That didn't work" : $"{failed.Count} of {jobs.Count} files failed";
            Notify(title, failed[0].Error ?? "Unknown error. Details are in the log.", Forms.ToolTipIcon.Warning);
        }
        else if (!_settings.NotifyOnFinish)
        {
            // Finished fine and the user has turned these off; failures above are always shown.
        }
        else if (succeeded.Count > 0)
        {
            string title = succeeded.Count == 1 ? "Done" : $"Done: {succeeded.Count} files";
            string body = string.Join(", ", succeeded.Select(j => j.Operation is CompressTo
                ? $"{Path.GetFileName(j.OutputPath)} ({FileSizes.Format(SizeOf(j.InputPath))} → {FileSizes.Format(SizeOf(j.OutputPath!))})"
                : Path.GetFileName(j.OutputPath)));
            if (cancelled > 0)
                body += $" ({cancelled} cancelled)";

            // Copy output file(s) to clipboard if enabled
            if (_settings.CopyToClipboardOnFinish)
            {
                try
                {
                    var filePaths = succeeded
                        .Where(j => !string.IsNullOrEmpty(j.OutputPath) && File.Exists(j.OutputPath!))
                        .Select(j => j.OutputPath!)
                        .ToArray();
                    if (filePaths.Length > 0)
                    {
                        var paths = new StringCollection();
                        paths.AddRange(filePaths);
                        Clipboard.SetFileDropList(paths);
                        body += filePaths.Length == 1 ? "\nFile copied to clipboard." : "\nFiles copied to clipboard.";
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Clipboard busy, ignore silently
                }
            }

            // With Open and Open folder buttons where Windows allows; otherwise the tray's own balloon.
            string first = succeeded[0].OutputPath!;
            if (!FinishedToast.TryShow(title, body, first, single: succeeded.Count == 1, canUndo: _undo.Contains(first)))
                Notify(title, body + "\nClick to show.", Forms.ToolTipIcon.Info, openOnClick: first);
        }
        else
        {
            Notify("Cancelled", $"{cancelled} file(s) were not converted.", Forms.ToolTipIcon.Info);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _releaseTimer;

    /// <summary>
    /// A few seconds after the last conversion (or a window) is done, gives the memory it used back
    /// to Windows: a 12-megapixel photo alone is about 48 MB of pixels. Left to itself, .NET keeps
    /// freed memory reserved for next time, so an idle Circularity would stay at its peak size.
    /// One full, compacting collection is cheap at that point, since nothing else is going on.
    /// </summary>
    private void ScheduleMemoryRelease()
    {
        if (_releaseTimer == null)
        {
            _releaseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _releaseTimer.Tick += (_, _) =>
            {
                _releaseTimer.Stop();
                // Try again later if something started in the meantime.
                if (_queue?.Unfinished.Count > 0 || _dialogOpen)
                    return;
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            };
        }
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Sends the last conversion's results to the Recycle Bin, and says what happened.</summary>
    private void UndoLast()
    {
        if (_undo.Count == 0)
        {
            Notify("Nothing to undo", "Only the most recent conversion can be undone, once.", Forms.ToolTipIcon.Info);
            return;
        }
        var outcome = _undo.Undo();
        UpdateUndoItem();
        Log.Write($"Undo: {outcome.Removed} moved to the Recycle Bin, {outcome.Changed} left alone, {outcome.Gone} already gone.");
        Notify(outcome.Removed > 0 ? "Undone" : "Nothing was undone", DescribeUndo(outcome), Forms.ToolTipIcon.Info);
    }

    internal static string DescribeUndo(UndoOutcome outcome)
    {
        static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
        var parts = new List<string>();
        if (outcome.Removed > 0)
            parts.Add($"Moved {Files(outcome.Removed)} to the Recycle Bin.");
        if (outcome.Changed > 0)
            parts.Add($"{Files(outcome.Changed)} had been changed since, so {(outcome.Changed == 1 ? "it was" : "they were")} left alone.");
        if (outcome.Gone > 0)
            parts.Add($"{Files(outcome.Gone)} had already been moved or deleted.");
        return string.Join(" ", parts);
    }

    private void UpdateUndoItem()
    {
        if (_undoItem == null)
            return;
        _undoItem.Enabled = _undo.Count > 0;
        _undoItem.Text = _undo.Count > 1 ? $"Undo last conversion ({_undo.Count} files)" : "Undo last conversion";
    }

    /// <summary>A button on the "Done" notification (or the notification itself) was clicked.</summary>
    private void OnFinishedToastAction(string action, string path)
    {
        if (action == FinishedToast.Undo)
        {
            if (_undo.Contains(path))
                UndoLast();
            else
                Notify("That can't be undone any more", "Only the most recent conversion can be undone, once.", Forms.ToolTipIcon.Info);
            return;
        }
        if (action == FinishedToast.Open && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // No app is set to open this kind of file; showing it is the next best thing.
                Log.Write($"Couldn't open {path}: {ex.Message}");
                ShowInExplorer(path);
            }
            return;
        }
        ShowInExplorer(path);
    }

    /// <summary>Opens a folder, or File Explorer with a file selected in its folder.</summary>
    private static void ShowInExplorer(string? target)
    {
        try
        {
            if (Directory.Exists(target))
                Process.Start("explorer.exe", $"\"{target}\"");
            else if (File.Exists(target))
                Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"Couldn't show {target}: {ex.Message}");
        }
    }
}
