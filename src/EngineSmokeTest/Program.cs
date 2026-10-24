using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Circularity;
using Circularity.Engine;

namespace EngineSmokeTest;

/// <summary>
/// Generates small sample files with FFmpeg, pushes every supported conversion through the real
/// ConversionQueue, and checks the results. Exit code 0 means everything passed.
///
///   dotnet run --project src/EngineSmokeTest
///   dotnet run --project src/EngineSmokeTest -- --render-previews &lt;folder&gt;   (PNGs of the UI)
/// </summary>
internal static class Program
{
    private static readonly List<(string Name, bool Passed, string Detail)> Results = new();

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render-previews")
            return Previews.Render(args[1]);
        if (args.Length == 1 && args[0] == "--drag-test")
            return DragTest.Run();
        if (args.Length == 1 && args[0] == "--startup-test")
            return StartupTest.Run();
        if (args.Length == 1 && args[0] == "--interaction-test")
            return InteractionTest.Run();
        if (args.Length == 2 && args[0] == "--render-readme-media")
            return ReadmeMedia.Render(args[1]);
        if (args.Length >= 1 && args[0] == "--update-test")
            return await UpdateTest.Run(args.Length > 1 ? args[1] : null);
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            AppIcon.WriteIco(args[1], new Settings().Accent);
            Console.WriteLine($"Icon written to {args[1]}");
            return 0;
        }

        var ffmpeg = new FfmpegConverter();
        if (ffmpeg.FfmpegPath == null)
        {
            Console.WriteLine("FFmpeg not found; install it or set FFMPEG_PATH.");
            return 2;
        }
        Console.WriteLine($"FFmpeg: {ffmpeg.FfmpegPath} (H.264 via {(ffmpeg.UsesWindowsH264 ? "Windows Media Foundation" : "x264")})");

        string dir = Path.Combine(Path.GetTempPath(), "CircularitySmoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "clip.mkv");
            string longVideo = Path.Combine(dir, "long.mkv");
            string audio = Path.Combine(dir, "tone.wav");
            string image = Path.Combine(dir, "pic.png");
            string photo = Path.Combine(dir, "photo.png");
            string unknown = Path.Combine(dir, "notes.xyz");

            // Sample files use encoders both the full development FFmpeg and the slim shipped one have.
            // 2 s of test pattern with an odd width (to exercise the even-size filter) plus a tone.
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=321x240:rate=25:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-shortest", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "pcm_s16le", video);
            // Long enough that the VP9 encode is still running when we cancel it.
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30:duration=60", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", longVideo);
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "sine=frequency=440:duration=2", audio);
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc=size=200x150", "-frames:v", "1", image);
            // A 12-megapixel frame with film-grain noise, which compresses like a real photo (badly).
            Ffmpeg(ffmpeg.FfmpegPath, "-f", "lavfi", "-i", "testsrc2=size=4000x3000", "-vf", "noise=alls=25:allf=t", "-frames:v", "1", photo);
            File.WriteAllText(unknown, "a format nothing understands");

            using var webPdf = new WebPdfConverter();
            using var queue = new ConversionQueue(new IConverter[] { new ImageConverter(ffmpeg), ffmpeg, new PdfConverter(), webPdf, new OfficeConverter() });

            // 1. Every supported conversion succeeds and produces a file FFmpeg can read back.
            var cases = new (string Input, string[] Targets)[]
            {
                (video, new[] { "mp4", "webm", "mov", "gif", "mp3" }),
                (audio, new[] { "mp3", "wav", "flac", "ogg", "m4a" }),
                (image, new[] { "jpg", "webp", "bmp", "gif", "tiff" }),
            };
            foreach (var (input, targets) in cases)
            {
                foreach (string target in targets)
                {
                    var batch = await Run(queue, new[] { input }, new ConvertTo(target));
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
                var batch = await Run(queue, new[] { audio }, new ConvertTo("mp3"));
                string? output = batch.Jobs[0].OutputPath;
                Check("second conversion gets a new name", Path.GetFileName(output) == "tone (1).mp3", Path.GetFileName(output) ?? "none");
            }

            // 3. Unsupported conversions fail cleanly with a readable message and leave no file behind.
            {
                var batch = await Run(queue, new[] { unknown }, new ConvertTo("pdf"));
                var job = batch.Jobs[0];
                Check("unsupported conversion fails cleanly",
                    job.State == JobState.Failed && job.Error!.Contains("isn't supported") && !File.Exists(Path.Combine(dir, "notes.pdf")),
                    job.Error ?? "");
            }

            // 4. Documents.
            await DocumentChecks(queue, dir, image);

            // 5. A multi-file batch reports once, with every job finished.
            {
                var batch = await Run(queue, new[] { image, image }, new ConvertTo("bmp"));
                Check("batch of two finishes together",
                    batch.Jobs.All(j => j.State == JobState.Succeeded) && batch.Jobs[0].OutputPath != batch.Jobs[1].OutputPath,
                    string.Join(", ", batch.Jobs.Select(j => Path.GetFileName(j.OutputPath))));
            }

            // 6. Resize and crop produce the right pixel sizes and names, for images (200×150) and video (321×240).
            {
                var imageEdits = new (string Name, Operation Operation, int Width, int Height)[]
                {
                    ("image resize fit 100x100", new ResizeTo(new ResizeSpec(ResizeKind.Fit, 100, 100)), 100, 75),
                    ("image resize stretch 100x100", new ResizeTo(new ResizeSpec(ResizeKind.Stretch, 100, 100)), 100, 100),
                    ("image resize fit upscales 400x400", new ResizeTo(new ResizeSpec(ResizeKind.Fit, 400, 400)), 400, 300),
                    ("image crop 50x40", new CropTo(new PixelRect(10, 10, 50, 40)), 50, 40),
                };
                foreach (var (name, operation, width, height) in imageEdits)
                {
                    var job = (await Run(queue, new[] { image }, operation)).Jobs[0];
                    if (job.State != JobState.Succeeded)
                    {
                        Check(name, false, $"{job.State}: {job.Error}");
                        continue;
                    }
                    var size = ImageConverter.ReadSize(job.OutputPath!);
                    Check(name, size == (width, height), $"{size.Width}x{size.Height} -> {Path.GetFileName(job.OutputPath)}");
                }

                var videoEdits = new (string Name, Operation Operation, int Width, int Height)[]
                {
                    // 321×240 fitted into 161×161 is 161×120; H.264 needs even sizes, so 160×120.
                    ("video resize fit 161x161", new ResizeTo(new ResizeSpec(ResizeKind.Fit, 161, 161)), 160, 120),
                    // Odd crop values are rounded down to even ones.
                    ("video crop 101x81", new CropTo(new PixelRect(11, 11, 101, 81)), 100, 80),
                };
                foreach (var (name, operation, width, height) in videoEdits)
                {
                    var job = (await Run(queue, new[] { video }, operation)).Jobs[0];
                    if (job.State != JobState.Succeeded)
                    {
                        Check(name, false, $"{job.State}: {job.Error}");
                        continue;
                    }
                    var size = await ffmpeg.ProbeVideoSizeAsync(job.OutputPath!);
                    Check(name, size == (width, height) && IsReadable(ffmpeg.FfmpegPath, job.OutputPath!),
                        $"{size.Width}x{size.Height} -> {Path.GetFileName(job.OutputPath)}");
                }

                var audioResize = (await Run(queue, new[] { audio }, new ResizeTo(new ResizeSpec(ResizeKind.Fit, 100, 100)))).Jobs[0];
                Check("resizing audio is refused", audioResize.State == JobState.Failed, audioResize.Error ?? "");
            }

            // 7. Compression: lower quality means smaller files, and a size limit is always met.
            {
                long Size(ConversionJob job) => job.OutputPath == null ? -1 : new FileInfo(job.OutputPath).Length;

                var high = (await Run(queue, new[] { photo }, new CompressTo(new CompressSpec(Quality: 90)))).Jobs[0];
                var low = (await Run(queue, new[] { photo }, new CompressTo(new CompressSpec(Quality: 30)))).Jobs[0];
                Check("compress: 30% quality is smaller than 90%",
                    high.State == JobState.Succeeded && low.State == JobState.Succeeded && Size(low) < Size(high),
                    $"{FileSizes.Format(Size(high))} at 90%, {FileSizes.Format(Size(low))} at 30% -> {Path.GetFileName(low.OutputPath)}");

                foreach (long limit in new[] { 300 * FileSizes.KB, 40 * FileSizes.KB })
                {
                    var job = (await Run(queue, new[] { photo }, new CompressTo(new CompressSpec(MaxBytes: limit)))).Jobs[0];
                    bool ok = job.State == JobState.Succeeded && Size(job) <= limit && IsReadable(ffmpeg.FfmpegPath, job.OutputPath!);
                    string detail = job.State == JobState.Succeeded
                        ? $"{FileSizes.Format(Size(job))}, {ImageConverter.ReadSize(job.OutputPath!).Width} px wide"
                        : $"{job.State}: {job.Error}";
                    Check($"compress: fits under {FileSizes.Format(limit)}", ok, detail);
                }

                var transparent = (await Run(queue, new[] { image }, new CompressTo(new CompressSpec(Quality: 80)))).Jobs[0];
                Check("compress: PNG input becomes JPG", transparent.OutputPath?.EndsWith(".jpg") == true, Path.GetFileName(transparent.OutputPath) ?? "none");
            }

            // 8. Progress is reported while FFmpeg runs, and cancelling kills it and removes the partial file.
            {
                double maxProgress = 0;
                queue.JobChanged += job =>
                {
                    if (job.InputPath == longVideo && job.State == JobState.Running)
                        maxProgress = Math.Max(maxProgress, job.Progress);
                };

                var finished = new TaskCompletionSource<ConversionBatch>();
                queue.BatchFinished += b => { if (b.Jobs[0].InputPath == longVideo) finished.TrySetResult(b); };
                var batch = queue.Enqueue(new[] { longVideo }, new ConvertTo("webm"));

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

    private static async Task DocumentChecks(ConversionQueue queue, string dir, string image)
    {
        const string Marker = "Pineapple";
        string markdown = Path.Combine(dir, "notes.md");
        string text = Path.Combine(dir, "readme.txt");
        string html = Path.Combine(dir, "page.html");
        string rtf = Path.Combine(dir, "letter.rtf");
        string csv = Path.Combine(dir, "scores.csv");

        // Three pages: raw HTML page breaks pass straight through Markdown.
        const string PageBreak = "\n\n<div style=\"page-break-after: always\"></div>\n\n";
        File.WriteAllText(markdown, $"# Notes\n\nA list:\n\n- {Marker}\n- Mango\n\n| a | b |\n|---|---|\n| 1 | 2 |" +
            PageBreak + "## Page two\n\nMore text." + PageBreak + "## Page three\n\nThe end.");
        File.WriteAllText(text, $"Plain text with a {Marker} in it.\nSecond line.");
        File.WriteAllText(html, $"<!doctype html><html><body><h1>Hello</h1><p>{Marker}</p></body></html>");
        File.WriteAllText(rtf, $"{{\\rtf1\\ansi{{\\fonttbl{{\\f0 Calibri;}}}}\\f0\\fs24 A letter about a {Marker}.\\par}}");
        File.WriteAllText(csv, "Name,Score\nAda,10\nLinus,9\n");

        async Task<ConversionJob> Convert(string file, string target) => (await Run(queue, new[] { file }, new ConvertTo(target))).Jobs[0];
        string Name(ConversionJob job) => job.State == JobState.Succeeded ? Path.GetFileName(job.OutputPath)! : $"{job.State}: {job.Error}";

        // Markdown → HTML and → PDF (laid out by Edge).
        var mdHtml = await Convert(markdown, "html");
        Check("md -> html", mdHtml.State == JobState.Succeeded && File.ReadAllText(mdHtml.OutputPath!).Contains("<h1"), Name(mdHtml));

        var mdPdf = await Convert(markdown, "pdf");
        bool mdPdfOk = mdPdf.State == JobState.Succeeded && PdfPages(mdPdf.OutputPath!) == 3 && PdfText(mdPdf.OutputPath!).Contains(Marker);
        Check("md -> pdf (3 pages, text kept)", mdPdfOk, mdPdf.State == JobState.Succeeded ? $"{PdfPages(mdPdf.OutputPath!)} pages" : Name(mdPdf));

        var txtPdf = await Convert(text, "pdf");
        Check("txt -> pdf", txtPdf.State == JobState.Succeeded && PdfText(txtPdf.OutputPath!).Contains(Marker), Name(txtPdf));
        var htmlPdf = await Convert(html, "pdf");
        Check("html -> pdf", htmlPdf.State == JobState.Succeeded && PdfText(htmlPdf.OutputPath!).Contains(Marker), Name(htmlPdf));
        if (!mdPdfOk || txtPdf.State != JobState.Succeeded)
            return; // Everything below needs these PDFs.

        // PDF → images: several pages become a folder, one page a single file.
        var pages = await Convert(mdPdf.OutputPath!, "png");
        string[] pngs = pages.State == JobState.Succeeded && Directory.Exists(pages.OutputPath) ? Directory.GetFiles(pages.OutputPath!, "*.png") : Array.Empty<string>();
        Check("pdf (3 pages) -> folder of 3 png", pngs.Length == 3 && pngs.All(p => ImageConverter.ReadSize(p).Width > 500),
            pages.State == JobState.Succeeded ? $"{Path.GetFileName(pages.OutputPath)}: {string.Join(", ", pngs.Select(Path.GetFileName))}" : Name(pages));

        var single = await Convert(txtPdf.OutputPath!, "jpg");
        Check("pdf (1 page) -> jpg file", single.State == JobState.Succeeded && File.Exists(single.OutputPath) && single.OutputPath!.EndsWith(".jpg"), Name(single));

        var pdfText = await Convert(mdPdf.OutputPath!, "txt");
        Check("pdf -> txt", pdfText.State == JobState.Succeeded && File.ReadAllText(pdfText.OutputPath!).Contains(Marker), Name(pdfText));

        // Images into PDFs.
        var imagePdf = await Convert(image, "pdf");
        Check("image -> pdf (1 page)", imagePdf.State == JobState.Succeeded && PdfPages(imagePdf.OutputPath!) == 1, Name(imagePdf));

        var combined = (await Run(queue, new[] { image }, new CombineIntoPdf(new[] { image, mdPdf.OutputPath!, image }))).Jobs[0];
        Check("combine image + 3-page pdf + image -> 5 pages",
            combined.State == JobState.Succeeded && PdfPages(combined.OutputPath!) == 5,
            combined.State == JobState.Succeeded ? $"{PdfPages(combined.OutputPath!)} pages -> {Path.GetFileName(combined.OutputPath)}" : Name(combined));

        // Office, if installed.
        if (Type.GetTypeFromProgID("Word.Application") != null)
        {
            var rtfPdf = await Convert(rtf, "pdf");
            Check("Word: rtf -> pdf", rtfPdf.State == JobState.Succeeded && PdfText(rtfPdf.OutputPath!).Contains(Marker), Name(rtfPdf));
            var rtfDocx = await Convert(rtf, "docx");
            bool isZip = rtfDocx.State == JobState.Succeeded && File.ReadAllBytes(rtfDocx.OutputPath!).AsSpan(0, 2).SequenceEqual("PK"u8);
            Check("Word: rtf -> docx", isZip, Name(rtfDocx));
            if (isZip)
            {
                var docxPdf = await Convert(rtfDocx.OutputPath!, "pdf");
                Check("Word: docx -> pdf", docxPdf.State == JobState.Succeeded && PdfText(docxPdf.OutputPath!).Contains(Marker), Name(docxPdf));
                var docxTxt = await Convert(rtfDocx.OutputPath!, "txt");
                Check("Word: docx -> txt", docxTxt.State == JobState.Succeeded && File.ReadAllText(docxTxt.OutputPath!).Contains(Marker), Name(docxTxt));
            }
        }
        else
        {
            Console.WriteLine("skip Word checks (Word not installed)");
        }

        if (Type.GetTypeFromProgID("Excel.Application") != null)
        {
            var csvPdf = await Convert(csv, "pdf");
            Check("Excel: csv -> pdf", csvPdf.State == JobState.Succeeded && PdfText(csvPdf.OutputPath!).Contains("Linus"), Name(csvPdf));
        }
        else
        {
            Console.WriteLine("skip Excel checks (Excel not installed)");
        }
    }

    private static int PdfPages(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return document.NumberOfPages;
    }

    private static string PdfText(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join(" ", document.GetPages().Select(p => p.Text));
    }

    private static async Task<ConversionBatch> Run(ConversionQueue queue, IReadOnlyList<string> files, Operation operation)
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
            mine = queue.Enqueue(files, operation);
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
        Console.WriteLine(passed ? $"ok   {name}" : $"FAIL {name}  ({detail})");
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
