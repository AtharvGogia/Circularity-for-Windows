using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity.Engine;

/// <summary>Video and audio conversion, video resizing, cropping and compressing, and trimming, by running ffmpeg.exe.</summary>
internal sealed partial class FfmpegConverter : IConverter
{
    // scale=... rounds odd sizes down to even, which H.264 with yuv420p requires.
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    // H.264 via x264 when this FFmpeg has it (GPL builds, e.g. for development). The FFmpeg that ships
    // with Circularity is an LGPL build without x264, so it uses the H.264 encoder built into Windows
    // (Media Foundation) instead, which needs NV12 input.
    private static readonly string[] X264 = { "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p" };
    private static readonly string[] WindowsH264 = { "-c:v", "h264_mf", "-rate_control", "quality", "-quality", "70", "-pix_fmt", "nv12" };
    private static readonly string[] Aac = { "-c:a", "aac", "-b:a", "160k" };
    private static readonly string[] Vp9 = { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k" };
    private static readonly string[] FastStart = { "-movflags", "+faststart" };

    private readonly string[] _h264;
    private readonly Dictionary<string, string[]> _argsByTarget;

    private static readonly HashSet<string> AudioTargets = new() { "mp3", "wav", "flac", "ogg", "m4a" };

    /// <summary>Containers a resized/cropped video keeps; anything else (AVI, WMV) becomes MP4.</summary>
    private static readonly HashSet<string> EditableContainers = new() { "mp4", "mov", "mkv", "webm", "m4v" };

    private readonly string? _ffmpegPath;

    public FfmpegConverter(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? Locate();
        string encoders = _ffmpegPath == null ? "" : ListEncoders(_ffmpegPath);
        bool Has(string encoder) => Regex.IsMatch(encoders, $@"\s{Regex.Escape(encoder)}\s");
        UsesWindowsH264 = _ffmpegPath != null && !Has("libx264");
        CanEncodeWebp = Has("libwebp");
        CanEncodeAvif = Has("libaom-av1");
        _h264 = (UsesWindowsH264 ? WindowsH264 : X264).Concat(Aac).ToArray();
        _argsByTarget = new()
        {
            ["mp4"] = new[] { "-vf", EvenSize }.Concat(_h264).Concat(FastStart).ToArray(),
            ["mov"] = new[] { "-vf", EvenSize }.Concat(_h264).ToArray(),
            ["webm"] = Vp9,
            // Two-pass palette in one filter graph: much better colours than ffmpeg's default GIF palette.
            ["gif"] = new[] { "-an", "-vf", "fps=12,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0" },
            ["mp3"] = new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
            ["wav"] = new[] { "-vn", "-c:a", "pcm_s16le" },
            ["flac"] = new[] { "-vn", "-c:a", "flac" },
            ["ogg"] = new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
            ["m4a"] = new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        };
    }

    /// <summary>Full path of the ffmpeg.exe in use, or null if none was found.</summary>
    public string? FfmpegPath => _ffmpegPath;

    /// <summary>True when this FFmpeg has no x264, so H.264 goes through Windows' own encoder.</summary>
    public bool UsesWindowsH264 { get; }

    /// <summary>Whether this FFmpeg can write WebP images (used by <see cref="ImageConverter"/>).</summary>
    public bool CanEncodeWebp { get; }

    /// <summary>Whether this FFmpeg can write AVIF images (libaom), used by <see cref="ImageConverter"/>.</summary>
    public bool CanEncodeAvif { get; }

    /// <summary>
    /// Encodes an image (the image converter hands it a PNG) to AVIF as a single still picture. A
    /// see-through image keeps its transparency as AVIF's separate alpha picture.
    /// </summary>
    public Task EncodeAvifAsync(string inputPath, string outputPath, bool hasAlpha, CancellationToken cancellationToken)
    {
        // Constant quality (CRF 30 is visually close to the original at a fraction of a JPG's size),
        // with a speed setting that keeps a phone photo to a few seconds.
        var quality = new[] { "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-b:v", "0", "-cpu-used", "6", "-row-mt", "1" };
        var args = new List<string> { "-i", inputPath };
        if (hasAlpha)
        {
            args.AddRange(new[] { "-filter_complex", "[0:v]format=yuva444p,split[colour][a];[a]alphaextract[alpha]", "-map", "[colour]", "-map", "[alpha]" });
            args.AddRange(quality);
            args.AddRange(new[] { "-pix_fmt:0", "yuv444p", "-pix_fmt:1", "gray" });
        }
        else
        {
            args.AddRange(quality);
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        }
        args.Add(outputPath);
        return RunFfmpegAsync(args, null, cancellationToken);
    }

    /// <summary>Encodes an image FFmpeg can read (the image converter hands it a PNG) to WebP.</summary>
    public Task EncodeWebpAsync(string inputPath, string outputPath, CancellationToken cancellationToken) =>
        RunFfmpegAsync(new[] { "-i", inputPath, "-c:v", "libwebp", "-quality", "85", outputPath }, null, cancellationToken);

    /// <summary>
    /// Decodes a still image to a PNG, the right way up: for photos Windows has no codec for, such as
    /// HEIC without the HEVC extension, or AVIF. FFmpeg reads HEIC's tiled grids and rotation itself.
    /// Blocking, since it's called from the image converter's own thread; images take well under a second.
    /// </summary>
    /// <param name="mergeAlpha">The file is an AVIF with a separate alpha picture (see <see cref="HasAlphaPicture"/>) to put back.</param>
    public void DecodeImageToPng(string inputPath, string pngPath, bool mergeAlpha = false)
    {
        var startInfo = new ProcessStartInfo(RequireFfmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", inputPath };
        if (mergeAlpha)
            args.AddRange(new[] { "-filter_complex", "[0:v:0][0:v:1]alphamerge" });
        args.AddRange(new[] { "-frames:v", "1", pngPath });
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        string errors = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            throw new InvalidOperationException("Decoding the image took too long.");
        }
        if (process.ExitCode != 0 || !File.Exists(pngPath) || new FileInfo(pngPath).Length == 0)
            throw new InvalidOperationException(errors.Trim().Split('\n').LastOrDefault()?.Trim() ?? "FFmpeg couldn't read it.");
    }

    /// <summary>
    /// Whether an AVIF keeps its transparency as a second, "Alpha" picture, as AVIF does. Windows'
    /// decoder and a plain FFmpeg decode both read only the colour picture, so these need
    /// <see cref="DecodeImageToPng"/> with mergeAlpha to stay see-through.
    /// </summary>
    public bool HasAlphaPicture(string path)
    {
        if (_ffmpegPath == null)
            return false;
        try
        {
            var startInfo = new ProcessStartInfo(_ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-nostdin", "-i", path })
                startInfo.ArgumentList.Add(arg);
            using var process = Process.Start(startInfo)!;
            string info = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            // "Stream #0:1[0x2]: Video: av1 ..., gray(...)" with "title : Alpha" under it.
            return Regex.IsMatch(info, @"Stream #0:1\S*: Video: .*gray") || Regex.IsMatch(info, @"title\s*:\s*Alpha");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The output of "ffmpeg -encoders", or "" if it can't be run.</summary>
    private static string ListEncoders(string ffmpegPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-hide_banner", "-encoders" })
                startInfo.ArgumentList.Add(arg);
            using var process = Process.Start(startInfo)!;
            string list = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return list;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

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
        (FileKind.Video, ConvertTo convert) => _argsByTarget.ContainsKey(convert.Target),
        (FileKind.Audio, ConvertTo convert) => AudioTargets.Contains(convert.Target),
        (FileKind.Video, ResizeTo or CropTo or CompressTo) => true,
        (FileKind.Video or FileKind.Audio, TrimTo) => true,
        _ => false,
    };

    public string OutputExtension(string inputPath, Operation operation)
    {
        string ext = Formats.NormalizedExtension(inputPath);
        bool video = Formats.KindOf(inputPath) == FileKind.Video;
        return operation switch
        {
            ConvertTo convert => convert.Target,
            // Compressed videos are always H.264 MP4: the format that plays everywhere and shrinks best here.
            CompressTo => "mp4",
            // Copying the streams works in any container; re-encoding needs one we have settings for.
            TrimTo { Exact: false } => ext,
            TrimTo when !video => AudioTargets.Contains(ext) ? ext : "m4a",
            _ => EditableContainers.Contains(ext) ? ext : "mp4",
        };
    }

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (operation is TrimTo trim)
        {
            await RunFfmpegAsync(TrimArgs(inputPath, trim, outputPath), progress, cancellationToken, trim.End - trim.Start);
            progress.Report(1);
            return outputPath;
        }
        if (operation is CompressTo compress)
        {
            await CompressVideoAsync(inputPath, compress.Spec, outputPath, progress, cancellationToken);
            progress.Report(1);
            return outputPath;
        }

        var args = new List<string> { "-i", inputPath };
        switch (operation)
        {
            case ConvertTo convert:
                args.AddRange(_argsByTarget[convert.Target]);
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
        return outputPath;
    }

    /// <summary>Upright size of the first video stream, read from ffmpeg's description of the file.</summary>
    public async Task<(int Width, int Height)> ProbeVideoSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await ProbeAsync(path, cancellationToken);
        if (!info.HasVideo)
            throw new InvalidOperationException("Couldn't read the video's size.");
        return (info.Width, info.Height);
    }

    /// <summary>Length, upright picture size, frame rate and bitrate, from ffmpeg's description of the file.</summary>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
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
        return ParseInfo(info);
    }

    internal static MediaInfo ParseInfo(string info)
    {
        var duration = DurationRegex().Match(info);
        var length = duration.Success && TimeSpan.TryParse(duration.Groups[1].Value, CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero;
        var bitrate = BitrateRegex().Match(info);
        long bitsPerSecond = bitrate.Success ? long.Parse(bitrate.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 : 0;
        bool hasAudio = AudioStreamRegex().IsMatch(info);

        int width = 0, height = 0;
        double fps = 0;
        var size = VideoSizeRegex().Match(info);
        if (size.Success)
        {
            width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
            height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            var rate = FpsRegex().Match(info);
            if (rate.Success)
                double.TryParse(rate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

            // Phone videos are often stored sideways with a rotation flag; ffmpeg applies it when decoding.
            var rotation = RotationRegex().Match(info);
            if (rotation.Success &&
                double.TryParse(rotation.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) &&
                Math.Abs(Math.Round(degrees)) % 180 == 90)
            {
                (width, height) = (height, width);
            }
        }
        return new MediaInfo(length, width, height, fps, bitsPerSecond, hasAudio);
    }

    /// <summary>
    /// What a video compression aims for: the picture size and the video and audio bitrates. Worked out
    /// the same way for the dialog's size estimate and for the encode itself.
    ///
    /// By quality, the bitrate comes from how many pixels a second there are (more for "higher
    /// quality"), but never above most of what the video already uses, so the result is always smaller.
    /// For a target size, the bitrate is whatever fits; if that's too little for a sharp picture at this
    /// size, the picture is scaled down in steps until it isn't.
    /// </summary>
    public static VideoPlan PlanCompression(MediaInfo info, CompressSpec spec)
    {
        double seconds = Math.Max(0.5, info.Duration.TotalSeconds);
        double fps = info.Fps is > 1 and < 240 ? Math.Min(info.Fps, 60) : 30;
        int shortSide = Math.Max(2, Math.Min(info.Width, info.Height));

        // "1080p" means the shorter side, so a portrait phone video at 1080×1920 already counts as 1080p.
        int targetShort = spec.MaxHeight is int max && max < shortSide ? max : shortSide;
        long pixelsPerSecond(int side) => (long)(info.Width * (double)side / shortSide * info.Height * (double)side / shortSide * fps);

        long sourceVideo = Math.Max(info.BitsPerSecond - (info.HasAudio ? 128_000 : 0), (long)(info.BitsPerSecond * 0.75));
        long video, audio;
        if (spec.MaxBytes is long maxBytes)
        {
            long total = (long)(maxBytes * 8 / seconds * 0.93);
            audio = !info.HasAudio ? 0 : total < 600_000 ? Math.Min(64_000, total / 6) : 128_000;
            video = Math.Max(60_000, total - audio);
            foreach (int side in new[] { 1080, 720, 540, 480, 360 })
            {
                if (video >= 0.035 * pixelsPerSecond(targetShort) || targetShort <= 360)
                    break;
                if (side < targetShort)
                    targetShort = side;
            }
        }
        else
        {
            int quality = spec.Quality ?? 60;
            double bitsPerPixel = quality >= 75 ? 0.10 : quality >= 55 ? 0.07 : 0.045;
            double shrink = (double)targetShort / shortSide;
            long cap = sourceVideo > 0 ? (long)(sourceVideo * shrink * shrink * 0.85) : long.MaxValue;
            video = Math.Max(150_000, Math.Min((long)(bitsPerPixel * pixelsPerSecond(targetShort)), cap));
            audio = info.HasAudio ? (quality >= 55 ? 128_000 : 96_000) : 0;
        }

        int width = EvenLength((int)Math.Round(info.Width * (double)targetShort / shortSide));
        int height = EvenLength((int)Math.Round(info.Height * (double)targetShort / shortSide));
        return new VideoPlan(width, height, video, audio, (long)((video + audio) * seconds / 8));
    }

    /// <summary>
    /// Re-encodes a video to H.264 at the planned bitrate. For a target size, it checks the result and
    /// tries again a little lower if the encoder overshot, since bitrate control is never exact.
    /// Windows' encoder won't go below its own idea of the least quality, however low the bitrate, so
    /// with it each retry also makes the picture smaller, in proportion to the overshoot.
    /// </summary>
    private async Task CompressVideoAsync(string inputPath, CompressSpec spec, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(inputPath, cancellationToken);
        if (!info.HasVideo)
            throw new InvalidOperationException("That file has no video to compress.");
        var plan = PlanCompression(info, spec);
        long videoBits = plan.VideoBitsPerSecond;
        int width = plan.Width, height = plan.Height;

        for (int attempt = 0; ; attempt++)
        {
            var args = new List<string> { "-i", inputPath, "-vf", $"scale={width}:{height}" };
            args.AddRange(UsesWindowsH264
                ? new[] { "-c:v", "h264_mf", "-rate_control", "pc_vbr", "-b:v", Bits(videoBits), "-pix_fmt", "nv12" }
                : new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", Bits(videoBits), "-maxrate", Bits(videoBits * 3 / 2), "-bufsize", Bits(videoBits * 2), "-pix_fmt", "yuv420p" });
            args.AddRange(plan.AudioBitsPerSecond > 0 ? new[] { "-c:a", "aac", "-b:a", Bits(plan.AudioBitsPerSecond) } : new[] { "-an" });
            args.AddRange(FastStart);
            args.Add(outputPath);
            await RunFfmpegAsync(args, progress, cancellationToken);

            long size = new FileInfo(outputPath).Length;
            if (spec.MaxBytes is not long maxBytes || size <= maxBytes)
                return;
            if (attempt == 3)
                throw new InvalidOperationException($"Couldn't get the video under {FileSizes.Format(maxBytes)} (it came out at {FileSizes.Format(size)}).");
            double ratio = (double)maxBytes / size * 0.92;
            videoBits = Math.Max(40_000, (long)(videoBits * ratio));
            if (UsesWindowsH264 && width > 160)
            {
                // Size goes roughly with the number of pixels, so each side shrinks by the square root.
                width = Math.Max(160, (int)(width * Math.Sqrt(ratio)) / 2 * 2);
                height = Math.Max(2, (int)Math.Round(plan.Height * (double)width / plan.Width / 2) * 2);
            }
        }
    }

    private static string Bits(long bitsPerSecond) => $"{Math.Max(1, bitsPerSecond / 1000)}k";

    private IEnumerable<string> TrimArgs(string inputPath, TrimTo trim, string outputPath)
    {
        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        // -ss before -i jumps straight there; when re-encoding, ffmpeg still starts at exactly that time.
        var args = new List<string> { "-ss", Seconds(trim.Start), "-i", inputPath, "-t", Seconds(trim.End - trim.Start) };
        if (!trim.Exact)
        {
            args.AddRange(new[] { "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else if (Formats.KindOf(inputPath) == FileKind.Video)
        {
            args.AddRange(new[] { "-vf", EvenSize });
            args.AddRange(EditCodecArgs(outputPath));
        }
        else
        {
            args.AddRange(_argsByTarget[Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant()]);
        }
        args.Add(outputPath);
        return args;
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

    /// <summary>
    /// Saves the frame at a given time as a small image, for the trim window's previews. At the very end
    /// of a video there may be no frame left to show, so it steps back a little until there is.
    /// </summary>
    public async Task<bool> ExtractFrameAtAsync(string videoPath, TimeSpan at, string imagePath, int maxWidth, CancellationToken cancellationToken = default)
    {
        foreach (double back in new[] { 0, 0.3, 1 })
        {
            string seconds = Math.Max(0, at.TotalSeconds - back).ToString("0.###", CultureInfo.InvariantCulture);
            try
            {
                await RunFfmpegAsync(new[] { "-ss", seconds, "-i", videoPath, "-frames:v", "1", "-vf", $"scale='min({maxWidth},iw)':-2", imagePath }, null, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (new FileInfo(imagePath) is { Exists: true, Length: > 0 })
                return true;
        }
        return false;
    }

    private IEnumerable<string> EditCodecArgs(string outputPath)
    {
        string ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (ext == "webm")
            return Vp9;
        return ext is "mp4" or "m4v" or "mov" ? _h264.Concat(FastStart) : _h264;
    }

    /// <summary>H.264 with yuv420p needs even dimensions.</summary>
    private static int EvenLength(int value) => Math.Max(2, value & ~1);

    private string RequireFfmpeg() =>
        _ffmpegPath ?? throw new InvalidOperationException("FFmpeg wasn't found. Install it (winget install Gyan.FFmpeg) or set FFMPEG_PATH.");

    /// <param name="expectedLength">How long the output will be, when that isn't the input's length (a trim).</param>
    private async Task RunFfmpegAsync(IEnumerable<string> args, IProgress<double>? progress, CancellationToken cancellationToken,
        TimeSpan? expectedLength = null)
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
        long durationTicks = expectedLength?.Ticks ?? 0;
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

    // "Duration: 00:00:12.34, start: 0.000000, bitrate: 4521 kb/s"
    [GeneratedRegex(@"Duration:.*?bitrate: (\d+) kb/s")]
    private static partial Regex BitrateRegex();

    // "..., 29.97 fps, 30 tbr, ..." on the video stream's line.
    [GeneratedRegex(@"Stream #.*?Video:.*?, (\d+(?:\.\d+)?) fps")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"Stream #.*?Audio:")]
    private static partial Regex AudioStreamRegex();
}

/// <summary>What ffmpeg says about a file. Width and height are upright and 0 for audio-only files.</summary>
internal sealed record MediaInfo(TimeSpan Duration, int Width, int Height, double Fps, long BitsPerSecond, bool HasAudio)
{
    public bool HasVideo => Width > 0 && Height > 0;
}

/// <summary>The picture size, bitrates and resulting file size a video compression aims for.</summary>
internal sealed record VideoPlan(int Width, int Height, long VideoBitsPerSecond, long AudioBitsPerSecond, long EstimatedBytes);
