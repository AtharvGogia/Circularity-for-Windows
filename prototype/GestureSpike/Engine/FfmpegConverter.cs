using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GestureSpike.Engine;

/// <summary>Video and audio conversion by running ffmpeg.exe.</summary>
internal sealed partial class FfmpegConverter : IConverter
{
    // scale=... rounds odd sizes down to even, which H.264 with yuv420p requires.
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    private static readonly Dictionary<string, string[]> ArgsByTarget = new()
    {
        ["mp4"] = new[] { "-vf", EvenSize, "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" },
        ["mov"] = new[] { "-vf", EvenSize, "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k" },
        ["webm"] = new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k" },
        // Two-pass palette in one filter graph: much better colours than ffmpeg's default GIF palette.
        ["gif"] = new[] { "-an", "-vf", "fps=12,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0" },
        ["mp3"] = new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        ["wav"] = new[] { "-vn", "-c:a", "pcm_s16le" },
        ["flac"] = new[] { "-vn", "-c:a", "flac" },
        ["ogg"] = new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        ["m4a"] = new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
    };

    private static readonly HashSet<string> AudioTargets = new() { "mp3", "wav", "flac", "ogg", "m4a" };

    private readonly string? _ffmpegPath;

    public FfmpegConverter(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? Locate();
    }

    /// <summary>Full path of the ffmpeg.exe in use, or null if none was found.</summary>
    public string? FfmpegPath => _ffmpegPath;

    /// <summary>Looks for ffmpeg.exe: FFMPEG_PATH, next to the app, in tools\, then on PATH.</summary>
    public static string? Locate()
    {
        var candidates = new List<string>();

        string? fromEnv = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            candidates.Add(fromEnv);

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"));

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public bool CanConvert(string inputPath, string target) => Formats.KindOf(inputPath) switch
    {
        FileKind.Video => ArgsByTarget.ContainsKey(target),
        FileKind.Audio => AudioTargets.Contains(target),
        _ => false,
    };

    public async Task ConvertAsync(string inputPath, string target, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_ffmpegPath == null)
            throw new InvalidOperationException("FFmpeg wasn't found. Install it (winget install Gyan.FFmpeg) or set FFMPEG_PATH.");

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -y: the output already exists as an empty placeholder. -progress pipe:1 writes
        // machine-readable "key=value" progress lines to stdout.
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-y", "-i", inputPath })
            startInfo.ArgumentList.Add(arg);
        foreach (string arg in ArgsByTarget[target])
            startInfo.ArgumentList.Add(arg);
        foreach (string arg in new[] { "-progress", "pipe:1", "-nostats", outputPath })
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        // ffmpeg prints the input's length to stderr ("Duration: 00:01:23.45") before encoding starts.
        long durationTicks = 0;
        var errorTail = new Queue<string>();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            if (Interlocked.Read(ref durationTicks) == 0)
            {
                var match = DurationRegex().Match(e.Data);
                if (match.Success && TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var duration))
                    Interlocked.Exchange(ref durationTicks, duration.Ticks);
            }
            lock (errorTail)
            {
                errorTail.Enqueue(e.Data.Trim());
                if (errorTail.Count > 6)
                    errorTail.Dequeue();
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            long total = Interlocked.Read(ref durationTicks);
            if (total > 0 && e.Data != null && e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan("out_time_us=".Length), out long micros))
            {
                // 1 microsecond = 10 ticks.
                progress.Report(Math.Clamp(micros * 10.0 / total, 0, 1));
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            // Wait so ffmpeg has let go of the output file before the caller deletes it.
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string details;
            lock (errorTail)
                details = string.Join(" | ", errorTail.Where(line => line.Length > 0).TakeLast(3));
            throw new InvalidOperationException($"FFmpeg failed (exit code {process.ExitCode}): {details}");
        }

        progress.Report(1);
    }

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();
}
