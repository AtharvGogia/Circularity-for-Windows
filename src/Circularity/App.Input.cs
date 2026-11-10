using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// Files arriving: the gesture, the hotkey, and other launches.
public partial class App
{
    // Explorer launches one copy per selected file for "Convert with Circularity", each handing its
    // file over separately, so files arriving close together are gathered into one wheel.
    private readonly List<string> _incomingFiles = new();
    private System.Windows.Threading.DispatcherTimer? _incomingTimer;

    /// <summary>Another launch handed over files (open the wheel) or nothing (show Settings).</summary>
    private void OnLaunchedAgain(string[] lines)
    {
        if (lines.Contains(InstanceChannel.OpenSettingsMessage))
            OpenSettings();
        else
            ReceiveFiles(lines.Where(File.Exists).ToArray());
    }

    private void ReceiveFiles(string[] files)
    {
        if (files.Length == 0)
            return;
        _incomingFiles.AddRange(files.Except(_incomingFiles, StringComparer.OrdinalIgnoreCase));
        if (_incomingTimer == null)
        {
            _incomingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _incomingTimer.Tick += (_, _) =>
            {
                _incomingTimer.Stop();
                var batch = _incomingFiles.ToArray();
                _incomingFiles.Clear();
                ShowWheelAtCursor(batch);
            };
        }
        _incomingTimer.Stop();
        _incomingTimer.Start();
    }

    /// <summary>Ctrl+Alt+C: the wheel for whatever is selected in the active Explorer window or the desktop.</summary>
    private void OnHotkey()
    {
        if (_paused)
            return;
        var files = ExplorerSelection.ForForegroundWindow();
        if (files.Length == 0)
        {
            Notify("Nothing selected", "Select one or more files in File Explorer or on the desktop, then press Ctrl+Alt+C.",
                Forms.ToolTipIcon.Info);
            return;
        }
        ShowWheelAtCursor(files);
    }

    private void ShowWheelAtCursor(string[] files)
    {
        if (files.Length == 0 || _wheel == null)
            return;
        Native.GetCursorPos(out var cursor);
        _wheel.ShowForFiles(files, cursor.X, cursor.Y);
    }

    private void UpdateHotkey()
    {
        if (_hotkey == null)
            return;
        if (!_settings.HotkeyEnabled)
        {
            _hotkey.Unregister();
            return;
        }
        if (!_hotkey.IsRegistered && !_hotkey.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x43 /* C */))
        {
            Log.Write("Ctrl+Alt+C is already used by another app; the shortcut is unavailable.");
            Notify("Ctrl+Alt+C is taken", "Another app already uses that shortcut, so Circularity can't.", Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Runs on the hook thread, so it has to stay quick.</summary>
    private bool ShouldArm(GestureTrigger trigger)
    {
        if (_paused)
            return false;
        if (trigger == GestureTrigger.RightClick && !_rightClickEnabled)
            return false;
        if (trigger == GestureTrigger.Shift && !_shiftEnabled)
            return false;

        if (!IsFileDrag(out string holder))
        {
            Log.Write($"{trigger} ignored: the left button isn't dragging something ({holder} is following the mouse).");
            return false;
        }

        if (Native.SHQueryUserNotificationState(out int state) == 0 &&
            state is Native.QUNS_BUSY or Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
        {
            Log.Write($"{trigger} ignored: a full-screen app is running (state {state}).");
            return false;
        }

        string process = ForegroundProcessName();
        if (!AllowedProcesses.Contains(process))
        {
            Log.Write($"{trigger} ignored: foreground app '{process}' is not on the allowlist.");
            return false;
        }

        Log.Write($"{trigger} while dragging from '{process}'.");
        return true;
    }

    /// <summary>
    /// Whether the left button is carrying something (files, a link, a picture), rather than the app
    /// following the mouse itself: selecting text, drawing a selection box, moving a window or a tab.
    /// While Windows carries a drag, its own drag window holds the mouse; otherwise the app's control
    /// does. When neither can be seen, it's taken to be a drag, as it always was.
    /// </summary>
    private static bool IsFileDrag(out string holder)
    {
        uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var info = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(thread, ref info))
        {
            holder = "nothing";
            return true;
        }
        holder = (info.flags & Native.GUI_INMOVESIZE) != 0 ? "moving a window"
            : (info.flags & Native.GUI_INMENUMODE) != 0 ? "a menu"
            : info.hwndCapture == IntPtr.Zero ? "nothing"
            : Native.ClassNameOf(info.hwndCapture);
        return ArmsFor(info.flags, info.hwndCapture == IntPtr.Zero ? null : holder);
    }

    /// <param name="captureClass">The window class of whatever holds the mouse, or null for nothing.</param>
    internal static bool ArmsFor(int guiFlags, string? captureClass) =>
        (guiFlags & (Native.GUI_INMOVESIZE | Native.GUI_INMENUMODE)) == 0 &&
        captureClass is null or OleDragWindowClass;

    /// <summary>The window Windows' drag and drop (OLE) holds the mouse with while it carries a drag.</summary>
    internal const string OleDragWindowClass = "CLIPBRDWNDCLASS";

    private static string ForegroundProcessName()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }
}
