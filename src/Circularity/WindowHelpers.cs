using System.Windows;
using System.Windows.Interop;

namespace Circularity;

internal static class WindowHelpers
{
    /// <summary>Set when rendering windows offscreen for previews, so they don't take focus.</summary>
    public static bool SuppressForeground { get; set; }

    /// <summary>
    /// Brings a dialog to the front with keyboard focus. Windows normally refuses to let a background
    /// app take focus, and after a drop the foreground app is still Explorer, so we briefly share
    /// Explorer's input state (AttachThreadInput), which is what lets SetForegroundWindow succeed.
    /// </summary>
    public static void ForceForeground(Window window)
    {
        if (SuppressForeground)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint ourThread = Native.GetCurrentThreadId();

        if (foregroundThread != ourThread && foregroundThread != 0)
        {
            Native.AttachThreadInput(ourThread, foregroundThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.AttachThreadInput(ourThread, foregroundThread, false);
        }
        else
        {
            Native.SetForegroundWindow(hwnd);
        }
        window.Activate();
    }
}
