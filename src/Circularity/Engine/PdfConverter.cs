using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Writer;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace Circularity.Engine;

/// <summary>
/// PDF work that needs nothing installed:
///  - PDF → PNG/JPG, one image per page, with PDFium (see <see cref="PdfiumDocument"/>). Windows'
///    own renderer (Windows.Data.Pdf) was dropped: it draws with the GPU, and on some graphics
///    drivers it crashed the app as it closed.
///  - PDF → TXT with PdfPig
///  - image → PDF, and several images/PDFs combined into one PDF, with PdfPig's PDF builder
///  - smaller PDFs, by re-saving their pictures at a lower resolution with PDFium
/// (PdfPig is Apache-2.0. PDFsharp was avoided: from 6.5 on it is AGPL.)
/// </summary>
internal sealed class PdfConverter : IConverter
{
    /// <summary>Pages are rendered at twice their natural size (about 192 dpi), which keeps text sharp.</summary>
    private const double RenderScale = 2;

    /// <summary>Huge pages (posters, maps) are scaled down to this many pixels on their longest side.</summary>
    private const int MaxRenderSide = 10000;

    public bool CanHandle(string inputPath, Operation operation)
    {
        string ext = Formats.NormalizedExtension(inputPath);
        return operation switch
        {
            ConvertTo { Target: "png" or "jpg" or "txt" } or CompressTo => ext == "pdf",
            ConvertTo { Target: "pdf" } => Formats.KindOf(inputPath) == FileKind.Image,
            CombineIntoPdf combine => combine.Files.All(f => Formats.NormalizedExtension(f) == "pdf" || Formats.KindOf(f) == FileKind.Image),
            _ => false,
        };
    }

    public string OutputExtension(string inputPath, Operation operation) =>
        operation is ConvertTo convert ? convert.Target : "pdf";

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (operation)
        {
            case ConvertTo { Target: "png" or "jpg" } convert:
                return await RenderPagesAsync(inputPath, convert.Target, outputPath, progress, cancellationToken);

            case ConvertTo { Target: "txt" }:
                await Task.Run(() => File.WriteAllText(outputPath, ExtractText(inputPath), new UTF8Encoding(false)), cancellationToken);
                break;

            case ConvertTo { Target: "pdf" }:
                await Sta.Run(() => { BuildPdf(new[] { inputPath }, outputPath, progress); return 0; }, "PdfConverter");
                break;

            case CombineIntoPdf combine:
                await Sta.Run(() => { BuildPdf(combine.Files, outputPath, progress); return 0; }, "PdfConverter");
                break;

            case CompressTo compress:
                await Sta.Run(() => { Compress(inputPath, compress.Spec, outputPath, cancellationToken); return 0; }, "PdfConverter");
                break;

            default:
                throw new NotSupportedException($"Can't do '{operation.Describe()}' with PDFs.");
        }
        progress.Report(1);
        return outputPath;
    }

    /// <summary>
    /// One page becomes "report.png" in the reserved spot. Several pages go into a folder
    /// "report (pages)" as "page 01.png", "page 02.png"..., replacing the placeholder file.
    /// </summary>
    private static Task<string> RenderPagesAsync(string inputPath, string target, string outputPath, IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var pdf = PdfiumDocument.Open(inputPath);
            int count = pdf.PageCount;

            string? folder = null;
            if (count > 1)
            {
                File.Delete(outputPath);
                folder = OutputPaths.ReserveFolder(inputPath, "pages");
            }

            try
            {
                int digits = Math.Max(2, count.ToString().Length);
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string pagePath = folder == null ? outputPath : Path.Combine(folder, $"page {(i + 1).ToString().PadLeft(digits, '0')}.{target}");

                    BitmapEncoder encoder = target == "jpg" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(pdf.RenderPage(i, RenderScale, MaxRenderSide)));
                    using (var output = File.Create(pagePath))
                        encoder.Save(output);

                    progress.Report((i + 1) / (double)count);
                }
            }
            catch
            {
                if (folder != null)
                    TryDeleteFolder(folder);
                throw;
            }
            return folder ?? outputPath;
        }, cancellationToken);

    /// <summary>
    /// The resolution and JPEG quality for each compression level: "light" keeps pictures sharp
    /// enough to print, "strong" is fine for reading on screen.
    /// </summary>
    public static (int Dpi, int JpegQuality) CompressionLevel(int quality) =>
        quality >= 75 ? (200, 85) : quality >= 55 ? (150, 75) : (100, 60);

    private static void Compress(string inputPath, CompressSpec spec, string outputPath, CancellationToken cancellationToken)
    {
        var (dpi, jpegQuality) = CompressionLevel(spec.Quality ?? 60);
        using (var pdf = PdfiumDocument.Open(inputPath))
        {
            if (pdf.ShrinkImages(dpi, jpegQuality, cancellationToken) == 0)
                throw new InvalidOperationException("This PDF can't be made smaller: it has no large pictures to shrink.");
            pdf.SaveAs(outputPath);
        }
        if (new FileInfo(outputPath).Length >= new FileInfo(inputPath).Length)
            throw new InvalidOperationException("This PDF can't be made smaller: its pictures are already compact.");
        CheckLooksTheSame(inputPath, outputPath, cancellationToken);
    }

    /// <summary>
    /// Compares every page of the original and the compressed PDF, drawn small. Shrunk pictures only
    /// change a little; anything more means a page didn't survive being rewritten (some unusual PDFs
    /// don't), and then it's safer to hand back nothing than a damaged copy.
    /// </summary>
    private static void CheckLooksTheSame(string originalPath, string compressedPath, CancellationToken cancellationToken)
    {
        using var original = PdfiumDocument.Open(originalPath);
        using var compressed = PdfiumDocument.Open(compressedPath);
        if (original.PageCount != compressed.PageCount)
            throw new InvalidOperationException("Compressing changed the number of pages, so the PDF was left as it was.");
        for (int i = 0; i < original.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MeanDifference(original.RenderPage(i, 0.3, 600), compressed.RenderPage(i, 0.3, 600)) > 10)
                throw new InvalidOperationException($"Compressing would have changed how page {i + 1} looks, so the PDF was left as it was.");
        }
    }

    /// <summary>Average difference per colour channel between two pictures, 0 to 255 (255 if their sizes differ).</summary>
    private static double MeanDifference(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return 255;
        byte[] Pixels(BitmapSource s)
        {
            var bgr = new FormatConvertedBitmap(s, PixelFormats.Bgr32, null, 0);
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

    private static string ExtractText(string inputPath)
    {
        using var document = PdfPigDocument.Open(inputPath);
        // Reading order rather than raw drawing order, so columns and paragraphs come out readable.
        return string.Join(Environment.NewLine + Environment.NewLine,
            document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page).Trim()));
    }

    /// <summary>Appends every PDF's pages and one page per image, in order, then saves.</summary>
    private static void BuildPdf(System.Collections.Generic.IReadOnlyList<string> files, string outputPath, IProgress<double> progress)
    {
        // Source PDFs must stay open until Build(), which is when their pages are actually copied.
        var sources = new System.Collections.Generic.List<PdfPigDocument>();
        try
        {
            var builder = new PdfDocumentBuilder();
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                if (Formats.NormalizedExtension(file) == "pdf")
                {
                    var source = PdfPigDocument.Open(file);
                    sources.Add(source);
                    for (int page = 1; page <= source.NumberOfPages; page++)
                        builder.AddPage(source, page);
                }
                else
                {
                    AddImagePage(builder, file);
                }
                progress.Report((i + 1) / (double)(files.Count + 1));
            }
            File.WriteAllBytes(outputPath, builder.Build());
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
        }
    }

    /// <summary>A page exactly the size of the picture (at 96 dpi), filled by it.</summary>
    private static void AddImagePage(PdfDocumentBuilder builder, string file)
    {
        // Go through WIC so any readable format (HEIC, WebP...) works and EXIF rotation is applied.
        // Photos are embedded as JPEG; anything with transparency as PNG.
        BitmapSource image = ImageConverter.LoadOriented(file);
        bool hasAlpha = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32;
        BitmapEncoder encoder = hasAlpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(hasAlpha ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);

        double width = image.PixelWidth * 72.0 / 96, height = image.PixelHeight * 72.0 / 96;
        var page = builder.AddPage(width, height);
        var area = new PdfRectangle(0, 0, width, height);
        if (hasAlpha)
            page.AddPng(encoded.ToArray(), area);
        else
            page.AddJpeg(encoded.ToArray(), area);
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
