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
            await AppleFormatChecks(queue, ffmpeg.FfmpegPath, dir);
            await CompressAndTrimChecks(queue, ffmpeg, dir, longVideo, audio, photo);
            await SvgChecks(queue, dir);

            // The updater reads release tags with three or four numbers, and orders them properly.
            Version Tag(string tag) => UpdateChecker.TryParseVersion(tag, out var v) ? v : new Version(0, 0, 0, 0);
            Check("updater: v0.3.3.1 is newer than v0.3.3, v0.3.3 equals 0.3.3.0, v0.3.4 is newer still",
                Tag("v0.3.3.1") > Tag("v0.3.3") && Tag("v0.3.3") == new Version(0, 3, 3, 0) && Tag("v0.3.4") > Tag("v0.3.3.1") && Tag("0.3.3.1.5") == new Version(0, 0, 0, 0),
                $"{Tag("v0.3.3.1")}, {Tag("v0.3.3")}, {Tag("v0.3.4")}");

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

        // The rendered page really has the text on it (dark pixels on the white page), not a blank sheet.
        if (pngs.Length > 0)
        {
            double ink = InkFraction(pngs.OrderBy(p => p).First());
            Check("rendered page has text on it", ink > 0.001 && ink < 0.5, $"{ink:P2} dark pixels");
        }

        // PDFium is given UTF-8 paths, so names outside plain ASCII must work.
        string unicodeDir = Path.Combine(dir, "Документы 文档");
        Directory.CreateDirectory(unicodeDir);
        string unicodePdf = Path.Combine(unicodeDir, "résumé.pdf");
        File.Copy(txtPdf.OutputPath!, unicodePdf);
        var unicodeJpg = await Convert(unicodePdf, "jpg");
        Check("pdf -> jpg with a non-English path", unicodeJpg.State == JobState.Succeeded && File.Exists(unicodeJpg.OutputPath), Name(unicodeJpg));

        string broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7 this is not really a PDF");
        var brokenPng = await Convert(broken, "png");
        Check("damaged pdf fails with a readable message", brokenPng.State == JobState.Failed && brokenPng.Error?.Contains("damaged") == true,
            $"{brokenPng.State}: {brokenPng.Error}");

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

        // What the wheel offers for documents, using the same converters as the app.
        string Offer(params string[] names)
        {
            var (label, actions) = Formats.ActionsFor(names, queue.CanHandle);
            return $"{label}: {string.Join(" ", actions.Select(a => a.Label))}";
        }
        if (Type.GetTypeFromProgID("Word.Application") != null)
        {
            string docx = Offer("a.docx");
            Check("wheel offers Word buttons for a .docx", docx.Contains("PDF") && docx.Contains("TXT"), docx);
            string pptx = Offer("a.pptx");
            Check("wheel offers PDF for a .pptx", pptx.Contains("PDF"), pptx);
        }
        string mixed = Offer("a.pdf", "b.xlsx");
        Check("documents with nothing in common don't blame Office", mixed.StartsWith("No format in common"), mixed);

        WheelButtonChecks();
    }

    /// <summary>
    /// HEIC and AVIF photos, through Windows' codec where this PC has one and through FFmpeg as on a
    /// PC without it; and Apple's audio containers. The samples are in Samples\, since the bundled
    /// FFmpeg can read these formats but not write them.
    /// </summary>
    private static async Task AppleFormatChecks(ConversionQueue queue, string ffmpegPath, string dir)
    {
        string Sample(string name)
        {
            string copy = Path.Combine(dir, name);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", name), copy, overwrite: true);
            return copy;
        }
        async Task<ConversionJob> Do(string file, Operation operation) => (await Run(queue, new[] { file }, operation)).Jobs[0];
        string Describe(ConversionJob job) => job.State == JobState.Succeeded
            ? $"{ImageConverter.ReadSize(job.OutputPath!).Width}×{ImageConverter.ReadSize(job.OutputPath!).Height} {Path.GetFileName(job.OutputPath)}"
            : $"{job.State}: {job.Error}";
        bool Is(ConversionJob job, int width, int height) =>
            job.State == JobState.Succeeded && ImageConverter.ReadSize(job.OutputPath!) == (width, height);

        string upright = Sample("upright.heic"), rotated = Sample("rotated.heic"), avif = Sample("photo.avif");

        var heicPng = await Do(upright, new ConvertTo("png"));
        Check("heic -> png", Is(heicPng, 1200, 800), Describe(heicPng));
        var rotatedJpg = await Do(rotated, new ConvertTo("jpg"));
        Check("sideways heic comes out upright", Is(rotatedJpg, 800, 1200), Describe(rotatedJpg));

        // As on a PC without the HEIF/HEVC (or AV1) extensions: FFmpeg does the decoding.
        ImageConverter.SkipWindowsDecoder = true;
        try
        {
            var viaFfmpeg = await Do(rotated, new ConvertTo("png"));
            Check("heic without Windows' codec -> png, upright", Is(viaFfmpeg, 800, 1200), Describe(viaFfmpeg));
            var resized = await Do(upright, new ResizeTo(new ResizeSpec(ResizeKind.Fit, 600, 600)));
            Check("heic without Windows' codec: resize", Is(resized, 600, 400), Describe(resized));
            var compressed = await Do(upright, new CompressTo(new CompressSpec(Quality: 70)));
            Check("heic without Windows' codec: compress", compressed.State == JobState.Succeeded && compressed.OutputPath!.EndsWith(".jpg"), Describe(compressed));
            var avifPng = await Do(avif, new ConvertTo("png"));
            Check("avif without Windows' codec -> png", Is(avifPng, 600, 400), Describe(avifPng));
        }
        finally
        {
            ImageConverter.SkipWindowsDecoder = false;
        }

        foreach (var (name, target) in new[] { ("tone.caf", "mp3"), ("tone.aiff", "flac") })
        {
            var job = await Do(Sample(name), new ConvertTo(target));
            Check($"{Path.GetExtension(name).TrimStart('.')} (Apple audio) -> {target}",
                job.State == JobState.Succeeded && IsReadable(ffmpegPath, job.OutputPath!), job.State == JobState.Succeeded ? Path.GetFileName(job.OutputPath)! : $"{job.Error}");
        }

        var heicButtons = Formats.ActionsFor(new[] { "IMG_0001.HEIC" }, queue.CanHandle).Actions.Select(a => a.Label).ToList();
        Check("heic offers png/jpg but never heic", heicButtons.Contains("PNG") && heicButtons.Contains("JPG") && !heicButtons.Contains("HEIC"),
            string.Join(" ", heicButtons));
    }

    /// <summary>Compressing videos and PDFs, and trimming videos and audio.</summary>
    private static async Task CompressAndTrimChecks(ConversionQueue queue, FfmpegConverter ffmpeg, string dir, string longVideo, string audio, string photo)
    {
        async Task<ConversionJob> Do(string file, Operation operation) => (await Run(queue, new[] { file }, operation)).Jobs[0];
        string Name(ConversionJob job) => job.State == JobState.Succeeded ? Path.GetFileName(job.OutputPath)! : $"{job.State}: {job.Error}";
        long Bytes(string path) => new FileInfo(path).Length;

        // 5 s of 720p with film-grain noise at a deliberately high bitrate: something worth compressing.
        string clip = Path.Combine(dir, "holiday.webm");
        Ffmpeg(ffmpeg.FfmpegPath!, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=5", "-f", "lavfi", "-i", "sine=frequency=330:duration=5",
            "-vf", "noise=alls=12:allf=t", "-c:v", "libvpx-vp9", "-b:v", "6M", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus", clip);
        long clipBytes = Bytes(clip);

        var balanced = await Do(clip, new CompressTo(new CompressSpec(Quality: 60)));
        var balancedInfo = balanced.State == JobState.Succeeded ? await ffmpeg.ProbeAsync(balanced.OutputPath!) : null;
        Check("video compress (balanced) is smaller, same size picture",
            balancedInfo != null && Bytes(balanced.OutputPath!) < clipBytes && balancedInfo is { Width: 1280, Height: 720 } && IsReadable(ffmpeg.FfmpegPath!, balanced.OutputPath!),
            balancedInfo != null ? $"{FileSizes.Format(clipBytes)} -> {FileSizes.Format(Bytes(balanced.OutputPath!))}, {balancedInfo.Width}×{balancedInfo.Height}" : Name(balanced));

        long target = clipBytes / 4;
        var sized = await Do(clip, new CompressTo(new CompressSpec(MaxBytes: target)));
        Check("video compress to a target size stays under it", sized.State == JobState.Succeeded && Bytes(sized.OutputPath!) <= target,
            sized.State == JobState.Succeeded ? $"{FileSizes.Format(Bytes(sized.OutputPath!))} (limit {FileSizes.Format(target)})" : Name(sized));

        var small = await Do(clip, new CompressTo(new CompressSpec(Quality: 60, MaxHeight: 480)));
        var smallInfo = small.State == JobState.Succeeded ? await ffmpeg.ProbeAsync(small.OutputPath!) : null;
        Check("video compress to 480p", smallInfo?.Height == 480, smallInfo != null ? $"{smallInfo.Width}×{smallInfo.Height}" : Name(small));

        // Trims: exact and quick, of the 60 s clip; and of a sound.
        var exact = await Do(longVideo, new TrimTo(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), Exact: true));
        var exactLength = exact.State == JobState.Succeeded ? (await ffmpeg.ProbeAsync(exact.OutputPath!)).Duration.TotalSeconds : 0;
        Check("exact trim keeps 10-15 s", Math.Abs(exactLength - 5) < 0.2, exact.State == JobState.Succeeded ? $"{exactLength:0.00} s, {Path.GetFileName(exact.OutputPath)}" : Name(exact));

        var quick = await Do(longVideo, new TrimTo(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), Exact: false));
        var quickLength = quick.State == JobState.Succeeded ? (await ffmpeg.ProbeAsync(quick.OutputPath!)).Duration.TotalSeconds : 0;
        Check("quick trim is about 5 s and keeps the format", quickLength is > 4.5 and < 8 && quick.OutputPath!.EndsWith(".mkv"),
            quick.State == JobState.Succeeded ? $"{quickLength:0.00} s, {Path.GetFileName(quick.OutputPath)}" : Name(quick));

        // The trim window's previews: a frame mid-way, and one at the very end (where there's no frame left to show).
        var frames = new List<string>();
        foreach (double at in new[] { 2.5, 5.0 })
        {
            string frame = Path.Combine(dir, $"frame-{at}.png");
            if (await ffmpeg.ExtractFrameAtAsync(clip, TimeSpan.FromSeconds(at), frame, 480))
                frames.Add($"{at} s: {ImageConverter.ReadSize(frame).Width} px wide");
        }
        Check("trim previews: frames mid-way and at the end", frames.Count == 2 && frames.All(f => f.EndsWith("480 px wide")), string.Join("; ", frames));

        var sound = await Do(audio, new TrimTo(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), Exact: true));
        var soundLength = sound.State == JobState.Succeeded ? (await ffmpeg.ProbeAsync(sound.OutputPath!)).Duration.TotalSeconds : 0;
        Check("audio trim keeps 1 s", Math.Abs(soundLength - 1) < 0.05, sound.State == JobState.Succeeded ? $"{soundLength:0.000} s" : Name(sound));

        // A PDF page with a line of text and two pictures drawn far smaller than their pixel size, the
        // way scans and phone photos usually end up in PDFs: a 12-megapixel PNG and a JPEG.
        const string Marker = "Papaya";
        string pdf = Path.Combine(dir, "brochure.pdf");
        var photoPicture = ImageConverter.LoadOriented(photo);
        byte[] png = File.ReadAllBytes(photo);
        var jpegEncoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
        jpegEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(photoPicture));
        using (var jpegStream = new MemoryStream())
        {
            jpegEncoder.Save(jpegStream);
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText($"Brochure {Marker}", 18, new UglyToad.PdfPig.Core.PdfPoint(50, 790), font);
            page.AddPng(png, new UglyToad.PdfPig.Core.PdfRectangle(50, 420, 545, 760));
            page.AddJpeg(jpegStream.ToArray(), new UglyToad.PdfPig.Core.PdfRectangle(50, 60, 545, 400));
            File.WriteAllBytes(pdf, builder.Build());
        }

        var compressedPdf = await Do(pdf, new CompressTo(new CompressSpec(Quality: 60)));
        bool pdfOk = compressedPdf.State == JobState.Succeeded;
        Check("pdf compress is much smaller",
            pdfOk && Bytes(compressedPdf.OutputPath!) < Bytes(pdf) / 3,
            pdfOk ? $"{FileSizes.Format(Bytes(pdf))} -> {FileSizes.Format(Bytes(compressedPdf.OutputPath!))}" : Name(compressedPdf));
        if (pdfOk)
        {
            Check("compressed pdf keeps its text and pages", PdfPages(compressedPdf.OutputPath!) == 1 && PdfText(compressedPdf.OutputPath!).Contains(Marker),
                PdfText(compressedPdf.OutputPath!));
            // Both pictures still there: the rendered page looks like the original's.
            using var before = PdfiumDocument.Open(pdf);
            using var after = PdfiumDocument.Open(compressedPdf.OutputPath!);
            double difference = MeanDifference(before.RenderPage(0, 0.5, 2000), after.RenderPage(0, 0.5, 2000));
            Check("compressed pdf looks the same", difference < 6, $"average difference {difference:0.0} of 255");
        }

        // Nothing to shrink in a text-only PDF: a clear message, and no file left behind.
        string textOnly = Directory.GetFiles(dir, "notes*.pdf").First();
        var nothing = await Do(textOnly, new CompressTo(new CompressSpec(Quality: 60)));
        Check("text-only pdf says it can't be made smaller", nothing.State == JobState.Failed && nothing.Error!.Contains("can't be made smaller"), $"{nothing.State}: {nothing.Error}");

        // Which tools are offered for what.
        string Tools(params string[] names) => string.Join(" ", Formats.ActionsFor(names, queue.CanHandle).Actions.Where(a => a.IsTool).Select(a => a.Label));
        Check("videos get Trim and Compress", Tools("a.mp4") is var v && v.Contains("Trim") && v.Contains("Compress"), Tools("a.mp4"));
        Check("audio gets Trim", Tools("a.mp3") == "Trim", Tools("a.mp3"));
        Check("PDFs get Compress, Word files don't", Tools("a.pdf") == "Compress" && !Tools("a.docx").Contains("Compress"), $"pdf: {Tools("a.pdf")}; docx: {Tools("a.docx")}");
        Check("a mix of photos and videos gets neither", !Tools("a.jpg", "b.mp4").Contains("Compress") && !Tools("a.jpg", "b.mp4").Contains("Trim"), Tools("a.jpg", "b.mp4"));
    }

    /// <summary>
    /// SVGs: drawn at a useful size with their colours and transparency, edited into PNGs, refused
    /// clearly when broken, and never fetching anything they link to (a web address served here).
    /// </summary>
    private static async Task SvgChecks(ConversionQueue queue, string dir)
    {
        async Task<ConversionJob> Do(string file, Operation operation) => (await Run(queue, new[] { file }, operation)).Jobs[0];
        string Name(ConversionJob job) => job.State == JobState.Succeeded ? Path.GetFileName(job.OutputPath)! : $"{job.State}: {job.Error}";
        System.Windows.Media.Color PixelAt(string png, int x, int y)
        {
            var image = new System.Windows.Media.Imaging.FormatConvertedBitmap(ImageConverter.LoadOriented(png), System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return System.Windows.Media.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        // A 600 × 300 logo: orange on the left, blue on the right.
        string logo = Path.Combine(dir, "logo.svg");
        File.WriteAllText(logo, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"300\" viewBox=\"0 0 600 300\">" +
            "<rect width=\"300\" height=\"300\" fill=\"#ff8a1e\"/><rect x=\"300\" width=\"300\" height=\"300\" fill=\"#3b82f6\"/></svg>");
        var logoPng = await Do(logo, new ConvertTo("png"));
        bool logoOk = logoPng.State == JobState.Succeeded && ImageConverter.ReadSize(logoPng.OutputPath!) == (600, 300);
        Check("svg -> png at its own size, colours right",
            logoOk && PixelAt(logoPng.OutputPath!, 50, 150) is { R: > 240, B: < 60 } && PixelAt(logoPng.OutputPath!, 550, 150) is { B: > 230, R: < 80 },
            logoPng.State == JobState.Succeeded ? $"{ImageConverter.ReadSize(logoPng.OutputPath!)}" : Name(logoPng));

        // A 24 px icon: drawn at 1024 px, and the area around the shape stays see-through.
        string icon = Path.Combine(dir, "star.svg");
        File.WriteAllText(icon, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" width=\"24\" height=\"24\">" +
            "<circle cx=\"12\" cy=\"12\" r=\"8\" fill=\"#222\"/></svg>");
        var iconPng = await Do(icon, new ConvertTo("png"));
        bool iconOk = iconPng.State == JobState.Succeeded && ImageConverter.ReadSize(iconPng.OutputPath!) == (1024, 1024);
        Check("small svg is drawn at 1024 px, transparent around it",
            iconOk && PixelAt(iconPng.OutputPath!, 10, 10).A == 0 && PixelAt(iconPng.OutputPath!, 512, 512) is { A: 255, R: < 60 },
            iconPng.State == JobState.Succeeded ? $"{ImageConverter.ReadSize(iconPng.OutputPath!)}" : Name(iconPng));

        var resized = await Do(icon, new ResizeTo(new ResizeSpec(ResizeKind.Fit, 256, 256)));
        Check("resizing an svg gives a png (keeps transparency)", resized.State == JobState.Succeeded && resized.OutputPath!.EndsWith(".png"), Name(resized));

        string broken = Path.Combine(dir, "broken.svg");
        File.WriteAllText(broken, "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=");
        var brokenPng = await Do(broken, new ConvertTo("png"));
        Check("broken svg fails with a readable message", brokenPng.State == JobState.Failed && brokenPng.Error!.Contains("isn't a valid SVG"), $"{brokenPng.State}: {brokenPng.Error}");

        // An SVG linking to a picture, a style and a pattern on a web address that this test serves: none
        // of them may be fetched, and the SVG still converts.
        using var listener = new System.Net.HttpListener();
        int port = new Random().Next(40000, 60000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int requests = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    Interlocked.Increment(ref requests);
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    return; // Stopped.
                }
            }
        });
        string tracker = Path.Combine(dir, "tracker.svg");
        string site = $"http://127.0.0.1:{port}";
        File.WriteAllText(tracker, "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"200\" height=\"100\">" +
            $"<style>@import url({site}/a.css); .x {{ fill: url({site}/p.svg#g) }}</style>" +
            $"<rect width=\"200\" height=\"100\" fill=\"#3b82f6\"/><rect class=\"x\" width=\"20\" height=\"20\"/>" +
            $"<image width=\"100\" height=\"100\" xlink:href=\"{site}/tracker.png\"/><image width=\"100\" height=\"100\" href=\"{site}/other.png\"/></svg>");
        var trackerPng = await Do(tracker, new ConvertTo("png"));
        await Task.Delay(500);
        listener.Stop();
        Check("svg links to the web are never fetched", trackerPng.State == JobState.Succeeded && requests == 0,
            $"{Name(trackerPng)}, {requests} request(s) made");

        var svgButtons = Formats.ActionsFor(new[] { "logo.svg" }, queue.CanHandle).Actions.Select(a => a.Label).ToList();
        Check("svg offers PNG and PDF, never SVG", svgButtons.Contains("PNG") && svgButtons.Contains("PDF") && !svgButtons.Contains("SVG"), string.Join(" ", svgButtons));
    }

    /// <summary>Average difference per colour channel between two same-sized pictures, 0 to 255.</summary>
    private static double MeanDifference(System.Windows.Media.Imaging.BitmapSource a, System.Windows.Media.Imaging.BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return 255;
        byte[] Pixels(System.Windows.Media.Imaging.BitmapSource s)
        {
            var bgr = new System.Windows.Media.Imaging.FormatConvertedBitmap(s, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var bytes = new byte[bgr.PixelWidth * bgr.PixelHeight * 4];
            bgr.CopyPixels(bytes, bgr.PixelWidth * 4, 0);
            return bytes;
        }
        byte[] pa = Pixels(a), pb = Pixels(b);
        long total = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            if (i % 4 != 3)
                total += Math.Abs(pa[i] - pb[i]);
        }
        return total / (pa.Length * 0.75);
    }

    /// <summary>The user's button order, hidden buttons and saved presets, as the wheel sees them.</summary>
    private static void WheelButtonChecks()
    {
        static string[] Labels(WheelAction[] actions) => actions.Select(a => a.Label).ToArray();
        bool Anything(string _, Operation __) => true;

        var size = new Preset("p1", "Web 1600", Resize: new ResizeSpec(ResizeKind.Fit, 1600, 1600));
        var small = new Preset("p2", "Under 500 KB", Compress: new CompressSpec(MaxBytes: 500 * 1024));
        var settings = new Settings { Presets = { size, small } };

        var photo = Formats.ActionsFor(new[] { "a.jpg" }, Anything, settings).Actions;
        Check("presets appear for images", Labels(photo).Contains("Web 1600") && Labels(photo).Contains("Under 500 KB"), string.Join(" ", Labels(photo)));
        var clip = Formats.ActionsFor(new[] { "a.mp4" }, Anything, settings).Actions;
        Check("only resize presets appear for videos", Labels(clip).Contains("Web 1600") && !Labels(clip).Contains("Under 500 KB"), string.Join(" ", Labels(clip)));

        // Move GIF and the resize preset to the front, hide WEBP.
        settings.ButtonLayouts["Image"] = new ButtonLayout { Order = { "gif", "preset:p1" }, Hidden = { "webp" } };
        photo = Formats.ActionsFor(new[] { "a.jpg" }, Anything, settings).Actions;
        Check("user order comes first", Labels(photo).Take(2).SequenceEqual(new[] { "GIF", "Web 1600" }), string.Join(" ", Labels(photo)));
        Check("hidden buttons are left off", !Labels(photo).Contains("WEBP"), string.Join(" ", Labels(photo)));
        string first = Labels(Formats.ActionsFor(new[] { "a.mp4" }, Anything, settings).Actions)[0];
        Check("other kinds keep the built-in order", first == "WEBM", first);

        // Even moved to the very front, a dialog button never ends up right of More.
        settings.ButtonLayouts["Image"] = new ButtonLayout { Order = { "crop", "compress", "resize" } };
        photo = Formats.ActionsFor(new[] { "a.jpg" }, Anything, settings).Actions;
        var (ring, arc) = WheelView.Layout(photo);
        bool onLeft = ring.Select((target, slot) => (target, slot))
            .Where(x => x.target >= 0 && photo[x.target].OpensDialog).All(x => x.slot is 4 or 5);
        int firstDialogInArc = arc.ToList().FindIndex(i => photo[i].OpensDialog);
        bool arcLeft = firstDialogInArc < 0 || arc.Skip(firstDialogInArc).All(i => photo[i].OpensDialog) && firstDialogInArc >= arc.Length / 2;
        Check("dialog buttons stay left of More whatever the order", onLeft && arcLeft,
            $"ring: {string.Join(" ", ring.Select(i => i >= 0 ? photo[i].Label : "More"))}; arc: {string.Join(" ", arc.Select(i => photo[i].Label))}");

        var all = Formats.AllButtonsFor(FileKind.Image, new Settings());
        Check("settings list every image button", all.Count(a => a.Kind == WheelActionKind.Convert) == 7 && all.Any(a => a.Kind == WheelActionKind.Combine),
            string.Join(" ", all.Select(a => a.Label)));

        // Settings survive a save and load, presets and layouts included.
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        Check("presets and button order are saved", back.Presets.SequenceEqual(settings.Presets) && back.ButtonLayouts["Image"].Order.SequenceEqual(settings.ButtonLayouts["Image"].Order),
            $"{back.Presets.Count} presets");
    }

    /// <summary>Share of an image's pixels that are dark (darker than mid-grey).</summary>
    private static double InkFraction(string path)
    {
        var image = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad),
            System.Windows.Media.PixelFormats.Gray8, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight];
        image.CopyPixels(pixels, image.PixelWidth, 0);
        return pixels.Count(p => p < 128) / (double)pixels.Length;
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
