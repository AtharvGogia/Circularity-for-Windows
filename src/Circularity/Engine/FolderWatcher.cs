using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity.Engine;

/// <summary>
/// Watches the folders in Settings → Features → Watched folders, and reports each new file once it has
/// finished arriving (a download or a copy can take a while), gathered into one batch per folder when
/// several arrive together. Files already there when watching starts are left alone, and so are
/// Circularity's own results (see <see cref="OutputPaths.IsProduced"/>), so nothing is converted twice.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    /// <summary>
    /// New files, ready to convert, for one watched folder. Raised on a background thread.
    /// </summary>
    public event Action<WatchedFolder, IReadOnlyList<string>>? FilesArrived;

    // Downloads and copies in progress, and Office's lock files: never converted, whatever they're called later.
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut", ".aria2",
    };

    private readonly Func<string, bool> _isOwnResult;
    private readonly TimeSpan _settleTime;
    private readonly TimeSpan _batchDelay;
    private readonly TimeSpan _giveUpAfter;
    private readonly object _lock = new();
    private readonly List<Watch> _watches = new();
    private bool _disposed;

    /// <param name="isOwnResult">True for a file Circularity wrote, which is never picked up.</param>
    /// <param name="settleTime">How long a file's size has to stay the same before it counts as arrived.</param>
    /// <param name="batchDelay">How long to wait for more files before reporting a batch.</param>
    public FolderWatcher(Func<string, bool> isOwnResult, TimeSpan? settleTime = null, TimeSpan? batchDelay = null, TimeSpan? giveUpAfter = null)
    {
        _isOwnResult = isOwnResult;
        _settleTime = settleTime ?? TimeSpan.FromSeconds(1);
        _batchDelay = batchDelay ?? TimeSpan.FromSeconds(2);
        _giveUpAfter = giveUpAfter ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>Watches exactly these folders (the enabled ones), keeping watches that haven't changed.</summary>
    public void Update(IEnumerable<WatchedFolder> folders)
    {
        var wanted = folders.Where(f => f.Enabled).ToList();
        lock (_lock)
        {
            if (_disposed)
                return;
            foreach (var watch in _watches.Where(w => !wanted.Contains(w.Folder)).ToList())
            {
                watch.Dispose();
                _watches.Remove(watch);
            }
            foreach (var folder in wanted.Where(f => _watches.All(w => w.Folder != f)))
            {
                var watch = new Watch(this, folder);
                if (watch.Start())
                    _watches.Add(watch);
            }
        }
    }

    /// <summary>
    /// Whether a new file is worth waiting for: not a folder, a partial download, a hidden or temporary
    /// file, or one of Circularity's own results, and a kind of file Circularity knows.
    /// </summary>
    internal bool IsCandidate(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith('~') || name.StartsWith('.') || PartialExtensions.Contains(Path.GetExtension(path)))
            return false;
        if (Formats.KindOf(path) == FileKind.Unknown || _isOwnResult(path))
            return false;
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Gone already, or not ours to read.
        }
    }

    /// <summary>
    /// Waits until nothing is writing the file any more: its size and time stay the same for a moment,
    /// and it can be opened without sharing. False if it went away, or never settled.
    /// </summary>
    private async Task<bool> WaitUntilSettledAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _giveUpAfter;
        (long, DateTime)? last = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_settleTime, cancellationToken);
            var file = new FileInfo(path);
            if (!file.Exists)
                return false;
            var now = (file.Length, file.LastWriteTimeUtc);
            if (now == last && CanOpenAlone(path))
                return true;
            last = now;
        }
        Log.Write($"Watched folder: gave up waiting for {path} to finish arriving.");
        return false;
    }

    private static bool CanOpenAlone(string path)
    {
        try
        {
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var watch in _watches)
                watch.Dispose();
            _watches.Clear();
        }
    }

    /// <summary>One watched folder: its FileSystemWatcher, files on their way in, and the batch being gathered.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly FolderWatcher _owner;
        private readonly CancellationTokenSource _stop = new();
        private readonly HashSet<string> _arriving = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _batch = new();
        private FileSystemWatcher? _watcher;
        private Timer? _batchTimer;

        public Watch(FolderWatcher owner, WatchedFolder folder)
        {
            _owner = owner;
            Folder = folder;
        }

        public WatchedFolder Folder { get; }

        public bool Start()
        {
            try
            {
                _watcher = new FileSystemWatcher(Folder.Path)
                {
                    IncludeSubdirectories = Folder.IncludeSubfolders,
                    NotifyFilter = NotifyFilters.FileName,
                    // Room for a big copy's worth of names before Windows drops events.
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += (_, e) => Noticed(e.FullPath);
                // A finished download is usually renamed from ".crdownload" or ".part" to its real name.
                _watcher.Renamed += (_, e) => Noticed(e.FullPath);
                _watcher.Error += (_, e) => Log.Write($"Watched folder {Folder.Path}: {e.GetException().Message}");
                _watcher.EnableRaisingEvents = true;
                Log.Write($"Watching {Folder.Path}{(Folder.IncludeSubfolders ? " and its subfolders" : "")} → {Folder.Action}");
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Log.Write($"Can't watch {Folder.Path}: {ex.Message}");
                _watcher?.Dispose();
                return false;
            }
        }

        private void Noticed(string path)
        {
            if (!_owner.IsCandidate(path))
                return;
            lock (_arriving)
            {
                if (_stop.IsCancellationRequested || !_arriving.Add(path))
                    return;
            }
            _ = ArriveAsync(path);
        }

        private async Task ArriveAsync(string path)
        {
            try
            {
                if (await _owner.WaitUntilSettledAsync(path, _stop.Token) && !_owner._isOwnResult(path))
                {
                    lock (_batch)
                    {
                        _batch.Add(path);
                        _batchTimer ??= new Timer(_ => ReportBatch());
                        _batchTimer.Change(_owner._batchDelay, Timeout.InfiniteTimeSpan);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"Watched folder: couldn't check {path}: {ex.Message}");
            }
            finally
            {
                lock (_arriving)
                    _arriving.Remove(path);
            }
        }

        private void ReportBatch()
        {
            List<string> files;
            lock (_batch)
            {
                if (_stop.IsCancellationRequested || _batch.Count == 0)
                    return;
                files = _batch.ToList();
                _batch.Clear();
            }
            _owner.FilesArrived?.Invoke(Folder, files);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _watcher?.Dispose();
            lock (_batch)
            {
                _batchTimer?.Dispose();
                _batch.Clear();
            }
        }
    }
}
