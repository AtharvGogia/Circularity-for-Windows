using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Circularity.Engine;

/// <summary>
/// Writes HEIC through the Windows Imaging Component directly. WPF can drive WIC's HEIF encoder too,
/// but can't pass it options, and left to itself it saves at near-lossless quality: bigger than a
/// JPG of the same photo, which defeats the point of HEIC. Here the quality is set like a phone's.
/// </summary>
internal static class HeifWriter
{
    /// <summary>
    /// WIC's ImageQuality, 0 to 1. On a real photo, 0.6 comes out about a quarter smaller than the
    /// quality-92 JPG Circularity writes, and looks as good, which is the point of HEIC; 0.8 is
    /// already about the JPG's size, and the encoder's own default much bigger.
    /// </summary>
    public const float DefaultQuality = 0.6f;

    private static readonly Guid HeifContainer = new("e1e62521-6787-405b-a339-500715b5763f");
    private static readonly Guid Bgr32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90e");
    private static readonly Guid ImagingFactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");

    /// <summary>Saves an opaque picture as HEIC; must run on a single-threaded apartment thread.</summary>
    public static void Save(BitmapSource image, string path, float quality = DefaultQuality)
    {
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int width = pixels.PixelWidth, height = pixels.PixelHeight, stride = width * 4;
        var buffer = new byte[stride * height];
        pixels.CopyPixels(buffer, stride, 0);

        var factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingFactoryClass, throwOnError: true)!)!;
        IWICBitmapEncoder? encoder = null;
        IWICBitmapFrameEncode? frame = null;
        IntPtr options = IntPtr.Zero;
        IntPtr stream = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, StgmCreate | StgmWrite, 0x80 /* normal */, true, IntPtr.Zero, out stream));
            var container = HeifContainer;
            Marshal.ThrowExceptionForHR(factory.CreateEncoder(ref container, IntPtr.Zero, out encoder));
            Marshal.ThrowExceptionForHR(encoder.Initialize(stream, 2 /* no cache */));
            Marshal.ThrowExceptionForHR(encoder.CreateNewFrame(out frame, out options));

            Marshal.ThrowExceptionForHR(WriteQuality(options, quality));
            Marshal.ThrowExceptionForHR(frame.Initialize(options));
            Marshal.ThrowExceptionForHR(frame.SetSize((uint)width, (uint)height));
            var format = Bgr32;
            Marshal.ThrowExceptionForHR(frame.SetPixelFormat(ref format));
            if (format != Bgr32)
                throw new NotSupportedException("Windows' HEIF encoder wants a different pixel format.");
            Marshal.ThrowExceptionForHR(frame.WritePixels((uint)height, (uint)stride, (uint)buffer.Length, buffer));
            Marshal.ThrowExceptionForHR(frame.Commit());
            Marshal.ThrowExceptionForHR(encoder.Commit());
        }
        finally
        {
            if (options != IntPtr.Zero) Marshal.Release(options);
            if (frame != null) Marshal.ReleaseComObject(frame);
            if (encoder != null) Marshal.ReleaseComObject(encoder);
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// IPropertyBag2::Write("ImageQuality", quality), called straight through the
    /// object's vtable: the encoder's option bag doesn't answer .NET's QueryInterface for the
    /// interface it already is, so the usual COM wrapper can't be used.
    /// </summary>
    private static unsafe int WriteQuality(IntPtr bag, float quality)
    {
        var name = Marshal.StringToCoTaskMemUni("ImageQuality");
        try
        {
            var property = new PropBag2Raw { Name = name };
            var value = new Variant { Type = 4 /* VT_R4 */, Float = quality };
            // Slots 0-2 are IUnknown, 3 is Read, 4 is Write.
            var write = (delegate* unmanaged[Stdcall]<IntPtr, uint, PropBag2Raw*, Variant*, int>)(*(IntPtr**)bag)[4];
            return write(bag, 1, &property, &value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropBag2Raw
    {
        public uint Type;
        public ushort VarType;
        public ushort ClipFormat;
        public uint Hint;
        public IntPtr Name;
        public Guid Class;
    }

    /// <summary>A VARIANT holding a float (VT_R4): 24 bytes on x64, the value at offset 8.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public float Float;
    }

    private const uint StgmWrite = 0x1, StgmCreate = 0x1000;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IntPtr stream);


    // Only the methods used are spelled out; the others hold their places in the COM vtable.

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        [PreserveSig] int CreateEncoder(ref Guid containerFormat, IntPtr vendor, out IWICBitmapEncoder encoder);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapEncoder
    {
        [PreserveSig] int Initialize(IntPtr stream, int cacheOption);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        [PreserveSig] int CreateNewFrame(out IWICBitmapFrameEncode frame, out IntPtr options);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameEncode
    {
        [PreserveSig] int Initialize(IntPtr options);
        [PreserveSig] int SetSize(uint width, uint height);
        void SetResolution();
        [PreserveSig] int SetPixelFormat(ref Guid format);
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        [PreserveSig] int WritePixels(uint lineCount, uint stride, uint bufferSize, [MarshalAs(UnmanagedType.LPArray)] byte[] pixels);
        void WriteSource();
        [PreserveSig] int Commit();
    }

}
