using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.FileIO;

namespace Circularity;

/// <summary>
/// "Undo last conversion": the results of the last conversion that made any, ready to go to the Recycle
/// Bin. Originals are never changed, so undoing only ever removes results. A result that has been
/// changed since (edited, or replaced by another file of the same name) is left alone: it's someone's
/// work now, not just Circularity's output.
/// </summary>
internal sealed class UndoLastConversion
{
    private readonly Func<string, bool> _recycle;
    private List<Result> _results = new();

    /// <param name="recycle">Moves a file or folder to the Recycle Bin; true if it went. Replaceable for tests.</param>
    public UndoLastConversion(Func<string, bool>? recycle = null)
    {
        _recycle = recycle ?? Recycle;
    }

    /// <summary>How many results the next undo would remove; 0 when there's nothing to undo.</summary>
    public int Count => _results.Count;

    /// <summary>Whether this result is part of what the next undo would remove.</summary>
    public bool Contains(string path) => _results.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remembers these results (and forgets the ones before), as they are right now.</summary>
    public void Remember(IEnumerable<string> outputs)
    {
        var results = outputs.Select(Snapshot).OfType<Result>().ToList();
        if (results.Count > 0)
            _results = results;
    }

    /// <summary>Sends the remembered results to the Recycle Bin, and forgets them.</summary>
    public UndoOutcome Undo()
    {
        int removed = 0, changed = 0, gone = 0;
        foreach (var result in _results)
        {
            var now = Snapshot(result.Path);
            if (now == null)
                gone++;
            else if (now != result)
                changed++;
            else if (_recycle(result.Path))
            {
                removed++;
                // Gone: a new file with this name in a watched folder is someone else's, not a result.
                Engine.OutputPaths.Forget(result.Path);
            }
            else
                changed++;
        }
        _results = new();
        return new UndoOutcome(removed, changed, gone);
    }

    public void Forget() => _results = new();

    /// <summary>A file's size and time, or a folder's file count, total size and newest time (PDF pages); null if it isn't there.</summary>
    private static Result? Snapshot(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return new Result(path, file.Length, file.LastWriteTimeUtc);
            }
            if (Directory.Exists(path))
            {
                var files = new DirectoryInfo(path).GetFiles("*", System.IO.SearchOption.AllDirectories);
                // How many files, how big in all and when last written: any page added, removed or edited changes one.
                return new Result(path, files.Sum(f => f.Length), files.Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max(), files.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool Recycle(string path)
    {
        try
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Write($"Couldn't move {path} to the Recycle Bin: {ex.Message}");
            return false;
        }
    }

    private sealed record Result(string Path, long Size, DateTime Written, int Files = 1);
}

/// <param name="Removed">Moved to the Recycle Bin.</param>
/// <param name="Changed">Left alone, because they'd been changed since (or couldn't be moved).</param>
/// <param name="Gone">Already deleted or moved by the user.</param>
internal readonly record struct UndoOutcome(int Removed, int Changed, int Gone);
