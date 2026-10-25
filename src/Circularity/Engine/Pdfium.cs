using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Circularity.Engine;

/// <summary>
/// Renders PDF pages to bitmaps with PDFium (pdfium.dll, from the bblanchon.PDFium.Win32 package).
/// It draws on the CPU, so it behaves the same on every PC whatever the graphics driver.
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
}
