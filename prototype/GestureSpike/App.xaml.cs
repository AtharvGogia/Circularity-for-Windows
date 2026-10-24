using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace GestureSpike;

/// <summary>
/// Tray app that wires the mouse hook to the wheel. No main window: everything happens
/// through the gesture, the tray icon and its balloon notifications.
/// </summary>
public partial class App : Application
{
    // The gesture only arms when one of these apps is in the foreground (the app being dragged
    // from). Games and CAD tools use left+right chords themselves, so they must never be affected.
    private static readonly HashSet<string> AllowedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "olk", "outlook", "thunderbird",
    };

    private Mutex? _singleInstance;
    private MouseHook? _hook;
    private WheelWindow? _wheel;
    private Forms.NotifyIcon? _tray;
    private volatile bool _paused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\GestureSpike", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("GestureSpike is already running (look in the tray).", "GestureSpike");
            Shutdown();
            return;
        }

        Log.Write("---- GestureSpike starting ----");
        DispatcherUnhandledException += (_, args) => Log.Write($"Unhandled exception: {args.Exception}");

        _wheel = new WheelWindow();
        _wheel.FormatChosen += OnFormatChosen;

        _tray = CreateTray();

        _hook = new MouseHook { ShouldArm = ShouldArm };
        _hook.ChordDetected += (x, y) => Dispatcher.BeginInvoke(() => _wheel.ShowAt(x, y));
        _hook.ChordDragEnded += () => Dispatcher.BeginInvoke(() => _wheel.OnDragEnded());
        Log.Write("Mouse hook installed.");

        _tray.ShowBalloonTip(4000, "GestureSpike is running",
            "Left-drag a file, tap right-click, then drop it on a format.", Forms.ToolTipIcon.Info);
    }

    /// <summary>Runs on the hook thread, so it has to stay quick.</summary>
    private bool ShouldArm()
    {
        if (_paused)
            return false;

        if (Native.SHQueryUserNotificationState(out int state) == 0 &&
            state is Native.QUNS_BUSY or Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
        {
            Log.Write($"Chord ignored: a full-screen app is running (state {state}).");
            return false;
        }

        string process = ForegroundProcessName();
        if (!AllowedProcesses.Contains(process))
        {
            Log.Write($"Chord ignored: foreground app '{process}' is not on the allowlist.");
            return false;
        }

        Log.Write($"Chord detected while dragging from '{process}'.");
        return true;
    }

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

    private void OnFormatChosen(IReadOnlyList<string> files, string target)
    {
        if (!ImageConverter.CanWrite(target) || files.Any(f => Formats.KindOf(f) != FileKind.Image))
        {
            Notify($"Converting to {target.ToUpperInvariant()} isn't built yet",
                "The prototype only does image conversion. The gesture worked though!", Forms.ToolTipIcon.Info);
            return;
        }

        // WPF imaging needs an STA thread; keep it off the UI thread so the wheel stays responsive.
        var worker = new Thread(() =>
        {
            var done = new List<string>();
            var failed = new List<string>();
            foreach (string file in files)
            {
                try
                {
                    string output = ImageConverter.Convert(file, target);
                    done.Add(Path.GetFileName(output));
                    Log.Write($"Converted {file} -> {output}");
                }
                catch (Exception ex)
                {
                    failed.Add(Path.GetFileName(file));
                    Log.Write($"Failed to convert {file}: {ex}");
                }
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (failed.Count == 0)
                    Notify("Converted", string.Join(", ", done), Forms.ToolTipIcon.Info);
                else
                    Notify($"{failed.Count} file(s) failed", $"{string.Join(", ", failed)}. Details are in the log.", Forms.ToolTipIcon.Warning);
            });
        })
        {
            IsBackground = true,
            Name = "Converter",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var pause = new Forms.ToolStripMenuItem("Pause gesture") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _paused = pause.Checked;
            Log.Write(_paused ? "Paused." : "Resumed.");
        };
        menu.Items.Add(pause);

        menu.Items.Add("Open log", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        return new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GestureSpike",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void Notify(string title, string text, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(4000, title, text, icon);

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Log.Write("---- GestureSpike exiting ----");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
