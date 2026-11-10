using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Circularity;
using Circularity.Engine;

namespace Circularity.UnitTests;

/// <summary>A folder of its own for each test, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"circularity-test-{Guid.NewGuid():N}");

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, string text = "hello")
    {
        string path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class UndoLastConversionTests
{
    // Stands in for the Recycle Bin, so tests don't fill the real one.
    private static UndoLastConversion WithFakeBin(List<string> binned) => new(path =>
    {
        binned.Add(path);
        File.Delete(path);
        return true;
    });

    [Fact]
    public void Undo_RemovesTheResults_AndOnlyOnce()
    {
        using var temp = new TempFolder();
        var binned = new List<string>();
        var undo = WithFakeBin(binned);
        string a = temp.File("a.png"), b = temp.File("b.png");

        undo.Remember(new[] { a, b });
        Assert.Equal(2, undo.Count);
        Assert.True(undo.Contains(a));

        var outcome = undo.Undo();
        Assert.Equal(new UndoOutcome(2, 0, 0), outcome);
        Assert.Equal(new[] { a, b }, binned);
        Assert.False(File.Exists(a));
        Assert.Equal(0, undo.Count);
        Assert.Equal(new UndoOutcome(0, 0, 0), undo.Undo());
    }

    [Fact]
    public void Undo_LeavesChangedResultsAlone()
    {
        using var temp = new TempFolder();
        var binned = new List<string>();
        var undo = WithFakeBin(binned);
        string edited = temp.File("edited.png"), gone = temp.File("gone.png"), untouched = temp.File("untouched.png");

        undo.Remember(new[] { edited, gone, untouched });
        File.AppendAllText(edited, " and more");
        File.Delete(gone);

        var outcome = undo.Undo();
        Assert.Equal(new UndoOutcome(Removed: 1, Changed: 1, Gone: 1), outcome);
        Assert.Equal(new[] { untouched }, binned);
        Assert.True(File.Exists(edited));
    }

    [Fact]
    public void Remember_OnlyTheNewestResults()
    {
        using var temp = new TempFolder();
        var undo = WithFakeBin(new List<string>());
        string first = temp.File("first.png"), second = temp.File("second.png");

        undo.Remember(new[] { first });
        undo.Remember(new[] { second });
        Assert.False(undo.Contains(first));
        Assert.True(undo.Contains(second));

        // Nothing that exists: the last results stay undoable.
        undo.Remember(new[] { Path.Combine(temp.Path, "never-written.png") });
        Assert.True(undo.Contains(second));
    }

    [Fact]
    public void Undo_RemovesAFolderOfPages()
    {
        using var temp = new TempFolder();
        string pages = Path.Combine(temp.Path, "report (pages)");
        Directory.CreateDirectory(pages);
        File.WriteAllText(Path.Combine(pages, "1.png"), "page");
        var binned = new List<string>();
        var undo = new UndoLastConversion(path =>
        {
            binned.Add(path);
            Directory.Delete(path, recursive: true);
            return true;
        });

        undo.Remember(new[] { pages });
        Assert.Equal(1, undo.Undo().Removed);
        Assert.Equal(new[] { pages }, binned);
    }

    [Fact]
    public void Undo_LeavesAFolderOfPagesAlone_WhenAPageChanged()
    {
        using var temp = new TempFolder();
        string pages = Path.Combine(temp.Path, "report (pages)");
        Directory.CreateDirectory(pages);
        string page = Path.Combine(pages, "1.png");
        File.WriteAllText(page, "page");
        var time = File.GetLastWriteTimeUtc(page);
        var undo = WithFakeBin(new List<string>());

        undo.Remember(new[] { pages });
        // Same number of files and the same time, but different content (and so size).
        File.WriteAllText(page, "edited page");
        File.SetLastWriteTimeUtc(page, time);

        Assert.Equal(new UndoOutcome(0, 1, 0), undo.Undo());
        Assert.True(Directory.Exists(pages));
    }

    [Fact]
    public void DescribeUndo_SaysWhatHappened()
    {
        Assert.Equal("Moved 2 files to the Recycle Bin.", App.DescribeUndo(new UndoOutcome(2, 0, 0)));
        Assert.Equal("Moved 1 file to the Recycle Bin. 1 file had been changed since, so it was left alone.",
            App.DescribeUndo(new UndoOutcome(1, 1, 0)));
        Assert.Equal("3 files had already been moved or deleted.", App.DescribeUndo(new UndoOutcome(0, 0, 3)));
    }
}

public class WatchedFolderTests
{
    [Fact]
    public void OperationFor_AConversionOrAPreset()
    {
        var preset = new Preset("abc12345", "Small", Resize: new ResizeSpec(ResizeKind.Fit, 800, 800));
        var settings = new Settings { Presets = new() { preset } };

        Assert.Equal(new ConvertTo("jpg"), new WatchedFolder("1", @"C:\x", "jpg").OperationFor(settings));
        Assert.Equal(preset.Operation, new WatchedFolder("1", @"C:\x", "preset:abc12345").OperationFor(settings));
        Assert.Null(new WatchedFolder("1", @"C:\x", "preset:deleted0").OperationFor(settings));
    }

    [Fact]
    public void Settings_KeepWatchedFolders_AndDropBrokenOnes()
    {
        var settings = Settings.FromJson("""
            {"Version": 2, "WatchedFolders": [
                {"Id": "a1", "Path": "C:\\Screenshots", "Action": "webp", "IncludeSubfolders": true, "Enabled": false},
                {"Id": "", "Path": "C:\\Nope", "Action": "jpg"},
                {"Id": "b2", "Path": "C:\\Nope", "Action": ""}
            ]}
            """);

        var folder = Assert.Single(settings.WatchedFolders);
        Assert.Equal(new WatchedFolder("a1", @"C:\Screenshots", "webp", IncludeSubfolders: true, Enabled: false), folder);
    }

    [Fact]
    public void Clone_DoesNotShareTheList()
    {
        var settings = new Settings { WatchedFolders = new() { new WatchedFolder("a1", @"C:\x", "jpg") } };
        var copy = settings.Clone();
        copy.WatchedFolders.Clear();
        Assert.Single(settings.WatchedFolders);
    }

    [Theory]
    [InlineData("photo.heic", "jpg", true)]
    [InlineData("photo.jpg", "jpg", false)]  // already that format
    [InlineData("song.mp3", "jpg", false)]   // not something a song becomes
    [InlineData("clip.mov", "mp4", true)]
    public void Suits_AConversion(string file, string target, bool suits)
    {
        Assert.Equal(suits, Formats.Suits(file, new ConvertTo(target), preset: null));
    }

    [Fact]
    public void Suits_APreset()
    {
        var resize = new Preset("r", "Small", Resize: new ResizeSpec(ResizeKind.Fit, 800, 800));
        var videoCompress = new Preset("c", "Share", Compress: new CompressSpec(Quality: 50), Kind: nameof(FileKind.Video));

        Assert.True(Formats.Suits("photo.jpg", resize.Operation, resize));
        Assert.False(Formats.Suits("song.mp3", resize.Operation, resize));
        Assert.True(Formats.Suits("clip.mp4", videoCompress.Operation, videoCompress));
        Assert.False(Formats.Suits("photo.jpg", videoCompress.Operation, videoCompress));
    }

    [Fact]
    public void AllTargets_HasEveryKind()
    {
        Assert.Contains("jpg", Formats.AllTargets);
        Assert.Contains("mp4", Formats.AllTargets);
        Assert.Contains("flac", Formats.AllTargets);
        Assert.Contains("docx", Formats.AllTargets);
        Assert.Equal(Formats.AllTargets.Count, Formats.AllTargets.Distinct().Count());
    }
}

public class OwnResultsTests
{
    [Fact]
    public void Results_AreRemembered_AndNothingElse()
    {
        using var temp = new TempFolder();
        string original = temp.File("photo.heic");

        string result = OutputPaths.Reserve(original, "jpg");
        string pages = OutputPaths.ReserveFolder(original, "pages");

        Assert.True(OutputPaths.IsProduced(result));
        Assert.True(OutputPaths.IsProduced(pages));
        Assert.True(OutputPaths.IsProduced(Path.Combine(pages, "photo 1.png")));
        Assert.False(OutputPaths.IsProduced(original));
        Assert.False(OutputPaths.IsProduced(Path.Combine(temp.Path, "someone else.jpg")));
    }

    [Fact]
    public void AForgottenResult_IsNoLongerOwn()
    {
        using var temp = new TempFolder();
        string result = OutputPaths.Reserve(temp.File("photo.heic"), "jpg");
        OutputPaths.Forget(result);
        Assert.False(OutputPaths.IsProduced(result));
    }

    [Fact]
    public void ATakenName_IsNotRemembered()
    {
        using var temp = new TempFolder();
        string original = temp.File("photo.heic");
        string theirs = temp.File("photo.jpg");

        string result = OutputPaths.Reserve(original, "jpg");

        Assert.NotEqual(theirs, result);
        Assert.False(OutputPaths.IsProduced(theirs));
    }
}

public class FolderWatcherTests
{
    private static FolderWatcher Fast(Func<string, bool>? isOwnResult = null) => new(
        isOwnResult ?? (_ => false),
        settleTime: TimeSpan.FromMilliseconds(150),
        batchDelay: TimeSpan.FromMilliseconds(300),
        giveUpAfter: TimeSpan.FromSeconds(10),
        retryEvery: TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task AFolderThatIsntThereYet_IsWatchedOnceItAppears()
    {
        using var temp = new TempFolder();
        string later = Path.Combine(temp.Path, "USB drive");
        var arrived = new ConcurrentQueue<string>();
        using var watcher = Fast();
        watcher.FilesArrived += (_, files) => { foreach (var f in files) arrived.Enqueue(f); };
        watcher.Update(new[] { new WatchedFolder("w", later, "png") });

        await Task.Delay(500);
        Directory.CreateDirectory(later);
        await Task.Delay(1000); // A retry or two.
        string photo = Path.Combine(later, "photo.jpg");
        File.WriteAllText(photo, "x");

        await WaitFor(() => arrived.Contains(photo));
    }

    [Fact]
    public async Task AFolderDeletedAndMadeAgain_IsWatchedAgain()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Screenshots");
        Directory.CreateDirectory(folder);
        var arrived = new ConcurrentQueue<string>();
        using var watcher = Fast();
        watcher.FilesArrived += (_, files) => { foreach (var f in files) arrived.Enqueue(f); };
        watcher.Update(new[] { new WatchedFolder("w", folder, "png") });

        string first = Path.Combine(folder, "first.jpg");
        File.WriteAllText(first, "x");
        await WaitFor(() => arrived.Contains(first));

        Directory.Delete(folder, recursive: true);
        await Task.Delay(1000);
        Directory.CreateDirectory(folder);
        await Task.Delay(1000);
        string second = Path.Combine(folder, "second.jpg");
        File.WriteAllText(second, "x");

        await WaitFor(() => arrived.Contains(second));
    }

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.jpg.crdownload", false)]
    [InlineData("movie.part", false)]
    [InlineData("~$report.docx", false)]
    [InlineData(".hidden.png", false)]
    [InlineData("notes.xyz", false)]
    public void IsCandidate_SkipsPartialAndUnknownFiles(string name, bool candidate)
    {
        using var temp = new TempFolder();
        using var watcher = Fast();
        Assert.Equal(candidate, watcher.IsCandidate(temp.File(name)));
    }

    [Fact]
    public void IsCandidate_SkipsOwnResults()
    {
        using var temp = new TempFolder();
        string own = temp.File("photo (converted).jpg");
        using var watcher = Fast(path => path == own);
        Assert.False(watcher.IsCandidate(own));
    }

    [Fact]
    public async Task NewFiles_ArriveTogether_AndOldOnesAreLeftAlone()
    {
        using var temp = new TempFolder();
        temp.File("already-here.jpg");
        var folder = new WatchedFolder("w", temp.Path, "png");
        var batches = new ConcurrentQueue<IReadOnlyList<string>>();
        using var watcher = Fast();
        watcher.FilesArrived += (f, files) =>
        {
            Assert.Equal(folder, f);
            batches.Enqueue(files);
        };
        watcher.Update(new[] { folder });

        string a = temp.File("a.jpg"), b = temp.File("b.heic");
        temp.File("c.txt.crdownload");
        // A download finishing: renamed to its real name.
        string partial = temp.File("d.jpg.part");
        string d = Path.Combine(temp.Path, "d.jpg");
        File.Move(partial, d);

        await WaitFor(() => batches.Sum(x => x.Count) >= 3);
        await Task.Delay(500); // anything else would have come by now
        var arrived = batches.SelectMany(x => x).OrderBy(x => x).ToList();
        Assert.Equal(new[] { a, b, d }.OrderBy(x => x), arrived);
    }

    [Fact]
    public async Task OwnResults_AreNeverReported()
    {
        using var temp = new TempFolder();
        var arrived = new ConcurrentQueue<string>();
        using var watcher = Fast(OutputPaths.IsProduced);
        watcher.FilesArrived += (_, files) => { foreach (var f in files) arrived.Enqueue(f); };
        watcher.Update(new[] { new WatchedFolder("w", temp.Path, "png") });

        string original = temp.File("photo.jpg");
        string result = OutputPaths.Reserve(original, "png");
        File.WriteAllText(result, "converted");

        await WaitFor(() => arrived.Contains(original));
        await Task.Delay(500);
        Assert.DoesNotContain(result, arrived);
    }

    [Fact]
    public async Task SubfoldersOnlyWhenAsked_AndPausedFoldersAreQuiet()
    {
        using var temp = new TempFolder();
        string inner = Path.Combine(temp.Path, "inner");
        Directory.CreateDirectory(inner);
        var arrived = new ConcurrentQueue<string>();
        using var watcher = Fast();
        watcher.FilesArrived += (_, files) => { foreach (var f in files) arrived.Enqueue(f); };

        watcher.Update(new[] { new WatchedFolder("w", temp.Path, "png") });
        string top = temp.File("top.jpg");
        File.WriteAllText(Path.Combine(inner, "skipped.jpg"), "x");
        await WaitFor(() => arrived.Contains(top));
        await Task.Delay(500);
        Assert.Single(arrived);

        watcher.Update(new[] { new WatchedFolder("w", temp.Path, "png", IncludeSubfolders: true) });
        string nested = Path.Combine(inner, "nested.jpg");
        File.WriteAllText(nested, "x");
        await WaitFor(() => arrived.Contains(nested));

        watcher.Update(new[] { new WatchedFolder("w", temp.Path, "png", Enabled: false) });
        temp.File("paused.jpg");
        await Task.Delay(800);
        Assert.Equal(2, arrived.Count);
    }

    [Fact]
    public async Task AFolderMovedIn_HasItsFilesPickedUp_WithSubfoldersOn()
    {
        using var temp = new TempFolder();
        using var elsewhere = new TempFolder();
        string trip = Path.Combine(elsewhere.Path, "trip");
        Directory.CreateDirectory(trip);
        File.WriteAllText(Path.Combine(trip, "beach.jpg"), "x");
        File.WriteAllText(Path.Combine(trip, "notes.xyz"), "not a known type");
        var arrived = new ConcurrentQueue<string>();
        using var watcher = Fast();
        watcher.FilesArrived += (_, files) => { foreach (var f in files) arrived.Enqueue(f); };
        watcher.Update(new[] { new WatchedFolder("w", temp.Path, "png", IncludeSubfolders: true) });

        string moved = Path.Combine(temp.Path, "trip");
        Directory.Move(trip, moved);

        string beach = Path.Combine(moved, "beach.jpg");
        await WaitFor(() => arrived.Contains(beach));
        await Task.Delay(500);
        Assert.Equal(new[] { beach }, arrived.ToArray());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the watcher.");
            await Task.Delay(50);
        }
    }
}
