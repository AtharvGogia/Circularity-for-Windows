using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GestureSpike.Engine;

namespace EngineSmokeTest;

/// <summary>
/// Generates small sample files with FFmpeg, pushes every supported conversion through the real
/// ConversionQueue, and checks the results. Exit code 0 means everything passed.
///
///   dotnet run --project prototype/EngineSmokeTest
/// </summary>
internal static class Program
{
    private static readonly List<(string Name, bool Passed, string Detail)> Results = new();

    private static async Task<int> Main()
    {
        var ffmpeg = new FfmpegConverter();
        if (ffmpeg.FfmpegPath == null)
        {
            Console.WriteLine("FFmpeg not found; install it or set FFMPEG_PATH.");
            return 2;
        }
        Console.WriteLine($"FFmpeg: {ffmpeg.FfmpegPath}");

        string dir = Path.Combine(Path.GetTempPath(), "GestureSpikeSmoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "clip.mkv");
            string longVideo = Path.Combine(dir, "long.mkv");
            string audio = Path.Combine(dir, "tone.wav");
            string image = Path.Combine(dir, "pic.png");
            string doc = Path.Combine(dir, "notes.docx");

            // 2 s of test pattern with an odd width (to exercise the even-size filter) plus a tone.
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=321x240:rate=25:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-shortest", "-c:v", "ffv1", "-c:a", "pcm_s16le", video);
            // Long enough that the VP9 encode is still running when we cancel it.
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30:duration=60", "-c:v", "libx264", "-preset", "ultrafast", longVideo);
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "sine=frequency=440:duration=2", audio);
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=200x150", "-frames:v", "1", image);
            File.WriteAllText(doc, "not really a docx");

            using var queue = new ConversionQueue(new IConverter[] { new ImageConverter(), ffmpeg });

            // 1. Every supported conversion succeeds and produces a file FFmpeg can read back.
            var cases = new (string Input, string[] Targets)[]
            {
                (video, new[] { "mp4", "webm", "mov", "gif", "mp3" }),
                (audio, new[] { "mp3", "wav", "flac", "ogg", "m4a" }),
                (image, new[] { "jpg", "bmp", "gif", "tiff" }),
            };
            foreach (var (input, targets) in cases)
            {
                foreach (string target in targets)
                {
                    var batch = await Run(queue, new[] { input }, target);
                    var job = batch.Jobs[0];
                    string name = $"{Path.GetFileName(input)} -> {target}";
                    if (job.State != JobState.Succeeded)
                        Check(name, false, $"{job.State}: {job.Error}");
                    else
                        Check(name, IsReadable(ffmpeg.FfmpegPath, job.OutputPath!), Path.GetFileName(job.OutputPath)!);
                }
            }

            // 2. Converting again never overwrites: the second copy gets a " (1)" name.
            {
                var batch = await Run(queue, new[] { audio }, "mp3");
                string? output = batch.Jobs[0].OutputPath;
                Check("second conversion gets a new name", Path.GetFileName(output) == "tone (1).mp3", Path.GetFileName(output) ?? "none");
            }

            // 3. Unsupported conversions fail cleanly with a readable message and leave no file behind.
            {
                var batch = await Run(queue, new[] { doc }, "pdf");
                var job = batch.Jobs[0];
                Check("unsupported conversion fails cleanly",
                    job.State == JobState.Failed && job.Error!.Contains("isn't supported") && !File.Exists(Path.Combine(dir, "notes.pdf")),
                    job.Error ?? "");
            }

            // 4. A multi-file batch reports once, with every job finished.
            {
                var batch = await Run(queue, new[] { image, image }, "bmp");
                Check("batch of two finishes together",
                    batch.Jobs.All(j => j.State == JobState.Succeeded) && batch.Jobs[0].OutputPath != batch.Jobs[1].OutputPath,
                    string.Join(", ", batch.Jobs.Select(j => Path.GetFileName(j.OutputPath))));
            }

            // 5. Progress is reported while FFmpeg runs, and cancelling kills it and removes the partial file.
            {
                double maxProgress = 0;
                queue.JobChanged += job =>
                {
                    if (job.InputPath == longVideo && job.State == JobState.Running)
                        maxProgress = Math.Max(maxProgress, job.Progress);
                };

                var finished = new TaskCompletionSource<ConversionBatch>();
                queue.BatchFinished += b => { if (b.Jobs[0].InputPath == longVideo) finished.TrySetResult(b); };
                var batch = queue.Enqueue(new[] { longVideo }, "webm");

                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (maxProgress == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                queue.CancelAll();

                var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var job = done.Jobs[0];
                // The partial file can only be deleted once FFmpeg has exited and released it.
                bool leftovers = Directory.GetFiles(dir, "long*.webm").Length > 0;
                Check("progress is reported", maxProgress > 0 && maxProgress < 1, $"{maxProgress:P1} before cancel");
                Check("cancel stops the job and removes the partial file",
                    job.State == JobState.Cancelled && !leftovers,
                    $"state {job.State}, leftovers {leftovers}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                Console.WriteLine($"(Couldn't delete {dir})");
            }
        }

        Console.WriteLine();
        foreach (var (name, passed, detail) in Results)
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name,-42} {detail}");
        int failures = Results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? $"All {Results.Count} checks passed." : $"{failures} of {Results.Count} checks failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<ConversionBatch> Run(ConversionQueue queue, IReadOnlyList<string> files, string target)
    {
        var finished = new TaskCompletionSource<ConversionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConversionBatch? mine = null;
        void OnFinished(ConversionBatch batch)
        {
            // The batch can finish inside Enqueue (unsupported files), before `mine` is assigned.
            if (mine == null || batch == mine)
                finished.TrySetResult(batch);
        }

        queue.BatchFinished += OnFinished;
        try
        {
            mine = queue.Enqueue(files, target);
            if (mine.Jobs.All(j => j.IsFinished))
                return mine;
            return await finished.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            queue.BatchFinished -= OnFinished;
        }
    }

    private static void Check(string name, bool passed, string detail)
    {
        Results.Add((name, passed, detail));
        Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {name}");
    }

    /// <summary>True if FFmpeg can decode the whole file without errors.</summary>
    private static bool IsReadable(string ffmpegPath, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return false;
        return RunProcess(ffmpegPath, "-v", "error", "-i", path, "-f", "null", "-").ExitCode == 0;
    }

    private static void Ffmpeg(string ffmpegPath, params string[] args)
    {
        var result = RunProcess(ffmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args).ToArray());
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't generate sample file: {result.Error}");
    }

    private static (int ExitCode, string Error) RunProcess(string exe, params string[] args)
    {
        var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }
}
