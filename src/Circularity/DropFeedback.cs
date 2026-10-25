using System;
using System.Runtime.InteropServices;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DVASPECT = System.Runtime.InteropServices.ComTypes.DVASPECT;
using FORMATETC = System.Runtime.InteropServices.ComTypes.FORMATETC;
using STGMEDIUM = System.Runtime.InteropServices.ComTypes.STGMEDIUM;
using TYMED = System.Runtime.InteropServices.ComTypes.TYMED;

namespace Circularity;

/// <summary>
/// The label Windows shows under a dragged file ("Copy to Documents"), set by the drop target. Over
/// the wheel it says what letting go will do: "Convert to PNG", or "Cancel" over the centre, without
/// the red no-entry badge. It also keeps Explorer's drag image drawn while over the wheel.
///
/// Windows only shows a label for a drop it's told will be accepted, so for these drags the centre
/// answers "copy" while hovered and "nothing" when actually dropped on.
///
/// Only drags that Windows draws (from File Explorer and most apps) show it; for others every call
/// here quietly does nothing.
/// </summary>
internal sealed class DropFeedback
{
    /// <summary>
    /// The icon shown next to the label (DROPIMAGETYPE). Invalid clears the label. There's an "icon
    /// only for no-drop" type too, but Windows hides the text with it, so cancel uses Label.
    /// </summary>
    public enum Icon { Invalid = -1, Copy = 1, Label = 6 }

    /// <summary>Whether Windows is drawing this drag (with its label), as it does for drags from File Explorer.</summary>
    public static bool IsShownFor(IDataObject data)
    {
        try
        {
            return data.GetDataPresent("DragWindow") || data.GetDataPresent("IsShowingLayered");
        }
        catch (COMException)
        {
            return false;
        }
    }

    private IDropTargetHelper? _helper;
    private (Icon, string, string)? _shown;

    public void Enter(IntPtr window, IDataObject data, DragDropEffects effect)
    {
        _shown = null;
        try
        {
            _helper ??= (IDropTargetHelper)new DragDropHelper();
            var point = Cursor();
            _helper.DragEnter(window, (ComDataObject)data, ref point, (int)effect);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _helper = null;
        }
    }

    public void Over(DragDropEffects effect)
    {
        try
        {
            var point = Cursor();
            _helper?.DragOver(ref point, (int)effect);
        }
        catch (COMException)
        {
        }
    }

    public void Leave(IDataObject data)
    {
        Describe(data, Icon.Invalid, "");
        try
        {
            _helper?.DragLeave();
        }
        catch (COMException)
        {
        }
    }

    public void Drop(IDataObject data, DragDropEffects effect)
    {
        Describe(data, Icon.Invalid, "");
        try
        {
            var point = Cursor();
            _helper?.Drop((ComDataObject)data, ref point, (int)effect);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
    }

    /// <summary>
    /// Sets the label. In <paramref name="message"/>, "%1" is replaced by <paramref name="insert"/>,
    /// which Windows shows in bold. <see cref="Icon.Invalid"/> clears it.
    /// </summary>
    public void Describe(IDataObject data, Icon icon, string message, string insert = "")
    {
        if (_shown == (icon, message, insert))
            return;
        _shown = (icon, message, insert);

        var description = new DROPDESCRIPTION { type = (int)icon, szMessage = message, szInsert = insert };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<DROPDESCRIPTION>());
        try
        {
            Marshal.StructureToPtr(description, memory, false);
            var format = new FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat("DropDescription").Id,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            // With release = true the data object owns the memory from here on.
            ((ComDataObject)data).SetData(ref format, ref medium, true);
            memory = IntPtr.Zero;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or NotSupportedException)
        {
            // The drag's source doesn't take drop descriptions.
        }
        finally
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    private static Native.POINT Cursor()
    {
        Native.GetCursorPos(out var point);
        return point;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DROPDESCRIPTION
    {
        public int type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szMessage;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szInsert;
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper
    {
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComDataObject dataObject, ref Native.POINT point, int effect);
        void DragLeave();
        void DragOver(ref Native.POINT point, int effect);
        void Drop(ComDataObject dataObject, ref Native.POINT point, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }
}
