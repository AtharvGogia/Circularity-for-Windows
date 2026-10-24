using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Circularity;

/// <summary>
/// Reads which files are selected in a File Explorer window or on the desktop, through the Windows
/// shell's own automation interface (Shell.Application).
/// </summary>
internal static class ExplorerSelection
{
    private const int SwcDesktop = 8;        // ShellWindowTypeConstants.SWC_DESKTOP
    private const int SwfoNeedDispatch = 1;  // ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH

    /// <summary>The selected files in the foreground Explorer window or the desktop; empty otherwise.</summary>
    public static string[] ForForegroundWindow() => ForWindow(Native.GetForegroundWindow());

    public static string[] ForWindow(IntPtr hwnd)
    {
        string windowClass = Native.ClassNameOf(hwnd);
        try
        {
            if (windowClass is "Progman" or "WorkerW")
                return Desktop();
            if (windowClass == "CabinetWClass")
                return ExplorerWindow(hwnd);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Write($"Couldn't read the Explorer selection: {ex.Message}");
        }
        return Array.Empty<string>();
    }

    private static string[] ExplorerWindow(IntPtr hwnd)
    {
        dynamic shell = CreateShell();
        var matches = new List<dynamic>();
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                if (new IntPtr(Convert.ToInt64(window.HWND)) == hwnd)
                    matches.Add(window);
            }
            catch (COMException)
            {
                // A window closing while we look; skip it.
            }
        }

        // Windows 11 tabs all share the window handle. The title shows the active tab's folder
        // ("Downloads - File Explorer"), so prefer the tab whose folder name it starts with.
        string title = Native.TitleOf(hwnd);
        dynamic? active = matches.Count == 1
            ? matches[0]
            : matches.FirstOrDefault(w => title.StartsWith((string)w.LocationName, StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        return active == null ? Array.Empty<string>() : Files(active.Document.SelectedItems());
    }

    private static string[] Desktop()
    {
        dynamic shell = CreateShell();
        dynamic windows = shell.Windows();
        object location = 0; // CSIDL_DESKTOP
        object root = null!;
        dynamic desktop = windows.FindWindowSW(ref location, ref root, SwcDesktop, out int desktopWindow, SwfoNeedDispatch);
        return Files(desktop.Document.SelectedItems());
    }

    /// <summary>Selected items that are actual files (not folders, Control Panel items or the like).</summary>
    private static string[] Files(dynamic items)
    {
        var files = new List<string>();
        foreach (dynamic item in items)
        {
            string path = item.Path;
            if (File.Exists(path))
                files.Add(path);
        }
        return files.ToArray();
    }

    private static dynamic CreateShell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
}
