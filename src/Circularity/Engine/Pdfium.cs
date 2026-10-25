using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Circularity.Engine;

/// <summary>
/// Renders PDF pages to bitmaps, and shrinks the pictures in PDFs, with PDFium (pdfium.dll, from the
/// bblanchon.PDFium.Win32 package). It draws on the CPU, so it behaves the same on every PC whatever
/// the graphics driver.
///
/// PDFium isn't thread-safe, so every call goes through one lock.
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool _initialized;

    private IntPtr _document;

    private PdfiumDocument(IntPtr document) => _document = document;

    /// <summary>Opens a PDF, or throws with a readable reason (password-protected, damaged...).</summary>
    public static PdfiumDocument Open(string path)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }
            IntPtr document = FPDF_LoadDocument(Utf8(path), null);
            if (document == IntPtr.Zero)
            {
                throw new InvalidOperationException(FPDF_GetLastError() switch
                {
                    FPDF_ERR_FILE => "The PDF couldn't be opened.",
                    FPDF_ERR_FORMAT => "The PDF is damaged or isn't really a PDF.",
                    FPDF_ERR_PASSWORD => "The PDF is password-protected.",
                    FPDF_ERR_SECURITY => "The PDF uses a kind of protection that isn't supported.",
                    _ => "The PDF couldn't be read.",
                });
            }
            return new PdfiumDocument(document);
        }
    }

    public int PageCount
    {
        get
        {
            lock (Gate)
                return FPDF_GetPageCount(Handle);
        }
    }

    /// <summary>
    /// Renders one page (0-based) on white, <paramref name="scale"/> times its natural size at 96 dpi,
    /// with its longest side kept within <paramref name="maxSide"/> pixels.
    /// </summary>
    public BitmapSource RenderPage(int index, double scale, int maxSide)
    {
        lock (Gate)
        {
            IntPtr page = FPDF_LoadPage(Handle, index);
            if (page == IntPtr.Zero)
                throw new InvalidOperationException($"Page {index + 1} of the PDF couldn't be read.");
            try
            {
                // Page sizes are in points (1/72 inch) and already account for the page's rotation.
                double width = FPDF_GetPageWidthF(page) * 96 / 72 * scale;
                double height = FPDF_GetPageHeightF(page) * 96 / 72 * scale;
                double fit = Math.Min(1, maxSide / Math.Max(width, height));
                int w = Math.Max(1, (int)Math.Round(width * fit)), h = Math.Max(1, (int)Math.Round(height * fit));

                IntPtr bitmap = FPDFBitmap_Create(w, h, 0);
                if (bitmap == IntPtr.Zero)
                    throw new OutOfMemoryException($"Page {index + 1} is too large to render.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, FPDF_ANNOT);
                    // BGRx, the same layout as WPF's Bgr32. Copied out, so PDFium's buffer can go.
                    var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null,
                        FPDFBitmap_GetBuffer(bitmap), FPDFBitmap_GetStride(bitmap) * h, FPDFBitmap_GetStride(bitmap));
                    image.Freeze();
                    return image;
                }
                finally
                {
                    FPDFBitmap_Destroy(bitmap);
                }
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Makes the PDF smaller by re-saving its big pictures as JPEGs at no more than
    /// <paramref name="maxDpi"/>, which is where nearly all of a large PDF's size usually is. Text,
    /// drawings and everything else stay exactly as they were. Left alone: small pictures, black and
    /// white scans (already tiny, and JPEG would blur them), pictures with transparency (JPEG has none),
    /// and JPEGs already near that resolution. Pictures used on several pages are stored once, so
    /// they're only shrunk once. Must run on an STA thread, for WPF's image scaling.
    /// </summary>
    /// <returns>How many pictures were replaced.</returns>
    public int ShrinkImages(int maxDpi, int jpegQuality, CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            int replaced = 0;
            int pageCount = FPDF_GetPageCount(Handle);
            for (int index = 0; index < pageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr page = FPDF_LoadPage(Handle, index);
                if (page == IntPtr.Zero)
                    continue;
                try
                {
                    int before = replaced;
                    foreach (IntPtr image in ImagesIn(page))
                    {
                        if (ShrinkImage(page, image, maxDpi, jpegQuality))
                            replaced++;
                    }
                    // The new JPEG is a new object; regenerating the page's content is what points the
                    // page at it (and lets the old one be left out when saving). Only pages that changed.
                    if (replaced > before && !FPDFPage_GenerateContent(page))
                        throw new InvalidOperationException($"Page {index + 1} couldn't be updated.");
                }
                finally
                {
                    FPDF_ClosePage(page);
                }
            }
            return replaced;
        }
    }

    /// <summary>Every picture on a page, including those inside grouped (form) objects.</summary>
    private static List<IntPtr> ImagesIn(IntPtr page)
    {
        var images = new List<IntPtr>();
        void Visit(IntPtr obj)
        {
            switch (FPDFPageObj_GetType(obj))
            {
                case FPDF_PAGEOBJ_IMAGE:
                    images.Add(obj);
                    break;
                case FPDF_PAGEOBJ_FORM:
                    for (int i = 0, n = FPDFFormObj_CountObjects(obj); i < n; i++)
                        Visit(FPDFFormObj_GetObject(obj, (uint)i));
                    break;
            }
        }
        for (int i = 0, n = FPDFPage_CountObjects(page); i < n; i++)
            Visit(FPDFPage_GetObject(page, i));
        return images;
    }

    private bool ShrinkImage(IntPtr page, IntPtr image, int maxDpi, int jpegQuality)
    {
        if (!FPDFImageObj_GetImageMetadata(image, page, out var meta) || meta.width * (long)meta.height < 200 * 200 || meta.bits_per_pixel <= 1)
            return false;
        if (FPDFPageObj_HasTransparency(image))
            return false;

        // How many pixels per inch it's shown at, from where it sits on the page (in points, 1/72").
        double dpi = Math.Min(meta.horizontal_dpi, meta.vertical_dpi);
        if (dpi <= 0 && FPDFPageObj_GetBounds(image, out float left, out float bottom, out float right, out float top) && right > left)
            dpi = meta.width / ((right - left) / 72.0);
        double scale = dpi > maxDpi ? maxDpi / dpi : 1;

        bool isJpeg = Filters(image).Contains("DCTDecode");
        if (isJpeg && scale > 0.8)
            return false;

        long rawSize = FPDFImageObj_GetImageDataRaw(image, IntPtr.Zero, 0);
        var picture = PictureOf(image);
        if (picture == null)
            return false;
        if (scale < 1)
        {
            int w = Math.Max(1, (int)Math.Round(picture.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(picture.PixelHeight * scale));
            picture = new TransformedBitmap(picture, new ScaleTransform((double)w / picture.PixelWidth, (double)h / picture.PixelHeight));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var jpeg = new MemoryStream();
        encoder.Save(jpeg);
        // Not worth it unless it's clearly smaller (a flat diagram can be smaller left as it was).
        if (rawSize > 0 && jpeg.Length > rawSize * 0.9)
            return false;

        byte[] bytes = jpeg.ToArray();
        GetBlock read = (_, position, buffer, size) =>
        {
            if (position + size > bytes.Length)
                return 0;
            Marshal.Copy(bytes, (int)position, buffer, (int)size);
            return 1;
        };
        var access = new FPDF_FILEACCESS { m_FileLen = (uint)bytes.Length, m_GetBlock = Marshal.GetFunctionPointerForDelegate(read) };
        var pages = new[] { page };
        bool ok = FPDFImageObj_LoadJpegFileInline(pages, 1, image, ref access);
        GC.KeepAlive(read);
        return ok;
    }

    /// <summary>The picture's own pixels (not including any mask), or null if PDFium can't decode it.</summary>
    private static BitmapSource? PictureOf(IntPtr image)
    {
        IntPtr bitmap = FPDFImageObj_GetBitmap(image);
        if (bitmap == IntPtr.Zero)
            return null;
        try
        {
            var format = FPDFBitmap_GetFormat(bitmap) switch
            {
                FPDFBitmap_Gray => PixelFormats.Gray8,
                FPDFBitmap_BGR => PixelFormats.Bgr24,
                FPDFBitmap_BGRx => PixelFormats.Bgr32,
                _ => (PixelFormat?)null, // With alpha: leave it alone, as JPEG would lose that.
            };
            if (format is not { } pixelFormat)
                return null;
            int w = FPDFBitmap_GetWidth(bitmap), h = FPDFBitmap_GetHeight(bitmap), stride = FPDFBitmap_GetStride(bitmap);
            var picture = BitmapSource.Create(w, h, 96, 96, pixelFormat, null, FPDFBitmap_GetBuffer(bitmap), stride * h, stride);
            picture.Freeze();
            return picture;
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    private static List<string> Filters(IntPtr image)
    {
        var names = new List<string>();
        for (int i = 0, n = FPDFImageObj_GetImageFilterCount(image); i < n; i++)
        {
            uint length = FPDFImageObj_GetImageFilter(image, i, null, 0);
            var buffer = new byte[length];
            FPDFImageObj_GetImageFilter(image, i, buffer, length);
            names.Add(Encoding.ASCII.GetString(buffer).TrimEnd('\0'));
        }
        return names;
    }

    /// <summary>Writes the whole document afresh (not as an update appended to the old one).</summary>
    public void SaveAs(string path)
    {
        lock (Gate)
        {
            using var output = File.Create(path);
            WriteBlock write = (_, data, size) =>
            {
                var chunk = new byte[size];
                Marshal.Copy(data, chunk, 0, (int)size);
                output.Write(chunk);
                return 1;
            };
            var writer = new FPDF_FILEWRITE { version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) };
            bool ok = FPDF_SaveAsCopy(Handle, ref writer, FPDF_NO_INCREMENTAL);
            GC.KeepAlive(write);
            if (!ok)
                throw new InvalidOperationException("The PDF couldn't be saved.");
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_document != IntPtr.Zero)
                FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
        }
    }

    private IntPtr Handle => _document != IntPtr.Zero ? _document : throw new ObjectDisposedException(nameof(PdfiumDocument));

    /// <summary>PDFium takes paths as null-terminated UTF-8.</summary>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    // --- pdfium.dll ---

    private const string Dll = "pdfium.dll";
    private const int FPDF_ERR_FILE = 2, FPDF_ERR_FORMAT = 3, FPDF_ERR_PASSWORD = 4, FPDF_ERR_SECURITY = 5;
    private const int FPDF_ANNOT = 0x01;

    [DllImport(Dll)] private static extern void FPDF_InitLibrary();
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadDocument(byte[] path, byte[]? password);
    [DllImport(Dll)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Dll)] private static extern uint FPDF_GetLastError();
    [DllImport(Dll)] private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Dll)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Dll)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll)] private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    // --- Editing pictures and saving (fpdf_edit.h, fpdf_save.h) ---

    private const int FPDF_PAGEOBJ_IMAGE = 3, FPDF_PAGEOBJ_FORM = 5;
    private const int FPDFBitmap_Gray = 1, FPDFBitmap_BGR = 2, FPDFBitmap_BGRx = 3;
    private const uint FPDF_NO_INCREMENTAL = 1 << 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FPDF_IMAGEOBJ_METADATA
    {
        public uint width, height;
        public float horizontal_dpi, vertical_dpi;
        public uint bits_per_pixel;
        public int colorspace, marked_content_id;
    }

    // "unsigned long" is 32 bits on Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public IntPtr m_GetBlock;
        public IntPtr m_Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FPDF_FILEWRITE
    {
        public int version;
        public IntPtr WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBlock(IntPtr param, uint position, IntPtr buffer, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlock(IntPtr self, IntPtr data, uint size);

    [DllImport(Dll)] private static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll)] private static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Dll)] private static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Dll)] private static extern int FPDFFormObj_CountObjects(IntPtr form);
    [DllImport(Dll)] private static extern IntPtr FPDFFormObj_GetObject(IntPtr form, uint index);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDFPageObj_HasTransparency(IntPtr obj);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDFImageObj_GetImageMetadata(IntPtr image, IntPtr page, out FPDF_IMAGEOBJ_METADATA metadata);
    [DllImport(Dll)] private static extern int FPDFImageObj_GetImageFilterCount(IntPtr image);
    [DllImport(Dll)] private static extern uint FPDFImageObj_GetImageFilter(IntPtr image, int index, byte[]? buffer, uint length);
    [DllImport(Dll)] private static extern uint FPDFImageObj_GetImageDataRaw(IntPtr image, IntPtr buffer, uint length);
    [DllImport(Dll)] private static extern IntPtr FPDFImageObj_GetBitmap(IntPtr image);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDFImageObj_LoadJpegFileInline(IntPtr[] pages, int count, IntPtr image, ref FPDF_FILEACCESS file);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE writer, uint flags);
    [DllImport(Dll)] private static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Dll)] private static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Dll)] private static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
}
