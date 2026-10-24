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

/// <summary>Video and audio conversion, plus video resizing and cropping, by running ffmpeg.exe.</summary>
internal sealed partial class FfmpegConverter : IConverter
{
    // scale=... rounds odd sizes down to even, which H.264 with yuv420p requires.
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    private static readonly string[] H264 = { "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k" };
    private static readonly string[] Vp9 = { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k" };
    private static readonly string[] FastStart = { "-movflags", "+faststart" };

    private static readonly Dictionary<string, string[]> ArgsByTarget = new()
    {
        ["mp4"] = new[] { "-vf", EvenSize }.Concat(H264).Concat(FastStart).ToArray(),
        ["mov"] = new[] { "-vf", EvenSize }.Concat(H264).ToArray(),
        ["webm"] = Vp9,
        // Two-pass palette in one filter graph: much better colours than ffmpeg's default GIF palette.
        ["gif"] = new[] { "-an", "-vf", "fps=12,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0" },
        ["mp3"] = new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        ["wav"] = new[] { "-vn", "-c:a", "pcm_s16le" },
        ["flac"] = new[] { "-vn", "-c:a", "flac" },
        ["ogg"] = new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        ["m4a"] = new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
    };

    private static readonly HashSet<string> AudioTargets = new() { "mp3", "wav", "flac", "ogg", "m4a" };

    /// <summary>Containers a resized/cropped video keeps; anything else (AVI, WMV) becomes MP4.</summary>
    private static readonly HashSet<string> EditableContainers = new() { "mp4", "mov", "mkv", "webm", "m4v" };

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

    public bool CanHandle(string inputPath, Operation operation) => (Formats.KindOf(inputPath), operation) switch
    {
        (FileKind.Video, ConvertTo convert) => ArgsByTarget.ContainsKey(convert.Target),
        (FileKind.Audio, ConvertTo convert) => AudioTargets.Contains(convert.Target),
        (FileKind.Video, ResizeTo or CropTo) => true,
        _ => false,
    };

    public string OutputExtension(string inputPath, Operation operation)
    {
        if (operation is ConvertTo convert)
            return convert.Target;
        string ext = Formats.NormalizedExtension(inputPath);
        return EditableContainers.Contains(ext) ? ext : "mp4";
    }

    public async Task RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-i", inputPath };
        switch (operation)
        {
            case ConvertTo convert:
                args.AddRange(ArgsByTarget[convert.Target]);
                break;

            case ResizeTo resize:
                var source = await ProbeVideoSizeAsync(inputPath, cancellationToken);
                var (width, height) = resize.Spec.Apply(source.Width, source.Height);
                args.AddRange(new[] { "-vf", $"scale={EvenLength(width)}:{EvenLength(height)}" });
                args.AddRange(EditCodecArgs(outputPath));
                break;

            case CropTo crop:
                var r = crop.Rect;
                args.AddRange(new[] { "-vf", $"crop={EvenLength(r.Width)}:{EvenLength(r.Height)}:{r.X & ~1}:{r.Y & ~1}" });
                args.AddRange(EditCodecArgs(outputPath));
                break;

            default:
                throw new NotSupportedException($"FFmpeg can't do '{operation.Describe()}'.");
        }
        args.Add(outputPath);

        await RunFfmpegAsync(args, progress, cancellationToken);
        progress.Report(1);
    }

    /// <summary>Upright size of the first video stream, read from ffmpeg's description of the file.</summary>
    public async Task<(int Width, int Height)> ProbeVideoSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(RequireFfmpeg())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-i", path })
            startInfo.ArgumentList.Add(arg);

        // With no output file ffmpeg prints the stream info and exits with an error code; that's expected.
        using var process = Process.Start(startInfo)!;
        string info = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var size = VideoSizeRegex().Match(info);
        if (!size.Success)
            throw new InvalidOperationException("Couldn't read the video's size.");
        int width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        int height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);

        // Phone videos are often stored sideways with a rotation flag; ffmpeg applies it when decoding.
        var rotation = RotationRegex().Match(info);
        if (rotation.Success &&
            double.TryParse(rotation.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) &&
            Math.Abs(Math.Round(degrees)) % 180 == 90)
        {
            (width, height) = (height, width);
        }
        return (width, height);
    }

    /// <summary>Saves one frame (about a second in) as an image, for the crop editor's preview.</summary>
    public async Task ExtractFrameAsync(string videoPath, string imagePath, CancellationToken cancellationToken = default)
    {
        // Very short clips have no frame at 1 s, so fall back to the first frame.
        foreach (string seek in new[] { "1", "0" })
        {
            try
            {
                await RunFfmpegAsync(new[] { "-ss", seek, "-i", videoPath, "-frames:v", "1", imagePath }, null, cancellationToken);
            }
            catch (InvalidOperationException) when (seek != "0")
            {
                continue;
            }
            if (new FileInfo(imagePath) is { Exists: true, Length: > 0 })
                return;
        }
        throw new InvalidOperationException("Couldn't read a frame from the video.");
    }

    private static IEnumerable<string> EditCodecArgs(string outputPath)
    {
        string ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (ext == "webm")
            return Vp9;
        return ext is "mp4" or "m4v" or "mov" ? H264.Concat(FastStart) : H264;
    }

    /// <summary>H.264 with yuv420p needs even dimensions.</summary>
    private static int EvenLength(int value) => Math.Max(2, value & ~1);

    private string RequireFfmpeg() =>
        _ffmpegPath ?? throw new InvalidOperationException("FFmpeg wasn't found. Install it (winget install Gyan.FFmpeg) or set FFMPEG_PATH.");

    private async Task RunFfmpegAsync(IEnumerable<string> args, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(RequireFfmpeg())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -y: outputs already exist as empty placeholders. -progress pipe:1 writes
        // machine-readable "key=value" progress lines to stdout.
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" }.Concat(args))
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
            if (progress != null && total > 0 && e.Data != null && e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
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
    }

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    // "Stream #0:0(und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080 [SAR 1:1 DAR 16:9], ..."
    [GeneratedRegex(@"Stream #.*?Video:.*?\b(\d{2,5})x(\d{2,5})\b")]
    private static partial Regex VideoSizeRegex();

    [GeneratedRegex(@"rotation of (-?\d+(?:\.\d+)?) degrees")]
    private static partial Regex RotationRegex();
}
