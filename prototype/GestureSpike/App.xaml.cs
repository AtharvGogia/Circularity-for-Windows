using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using GestureSpike.Engine;
using Forms = System.Windows.Forms;
using SystemIcons = System.Drawing.SystemIcons;

namespace GestureSpike;

/// <summary>
/// Tray app that wires the mouse hook to the wheel and the wheel to the conversion queue.
/// No main window: everything happens through the gesture, the tray icon and its notifications.
/// </summary>
public partial class App : Application
{
    // The gesture only arms when one of these apps is in the foreground (the app being dragged
    // from). Games and CAD tools use left+right chords themselves, so they must never be affected.
    private static readonly HashSet<string> AllowedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "olk", "outlook", "thunderbird",
    };

    private const string TrayTitle = "GestureSpike";

    private Mutex? _singleInstance;
    private MouseHook? _hook;
    private WheelWindow? _wheel;
    private ConversionQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _cancelItem;
    private volatile bool _paused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\GestureSpike", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("GestureSpike is already running (look in the tray).", TrayTitle);
            Shutdown();
            return;
        }

        Log.Write("---- GestureSpike starting ----");
        DispatcherUnhandledException += (_, args) => Log.Write($"Unhandled exception: {args.Exception}");

        var ffmpeg = new FfmpegConverter();
        Log.Write(ffmpeg.FfmpegPath != null
            ? $"Using FFmpeg at {ffmpeg.FfmpegPath}"
            : "FFmpeg not found; video and audio conversions will fail until it's installed.");

        _queue = new ConversionQueue(new IConverter[] { new ImageConverter(), ffmpeg });
        _queue.JobChanged += _ => Dispatcher.BeginInvoke(UpdateTrayStatus);
        _queue.BatchFinished += batch => Dispatcher.BeginInvoke(() => ReportBatch(batch));

        _wheel = new WheelWindow();
        _wheel.FormatChosen += (files, target) => _queue.Enqueue(files, target);

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

    /// <summary>Shows what's converting in the tray tooltip and enables "Cancel conversions".</summary>
    private void UpdateTrayStatus()
    {
        if (_tray == null || _queue == null)
            return;

        var unfinished = _queue.Unfinished;
        _cancelItem!.Enabled = unfinished.Count > 0;

        var running = unfinished.Where(j => j.State == JobState.Running).ToList();
        if (unfinished.Count == 0)
        {
            _tray.Text = TrayTitle;
            return;
        }

        string text;
        if (running.Count > 0)
        {
            var job = running[0];
            text = $"{Path.GetFileName(job.InputPath)} → {job.Target.ToUpperInvariant()} {job.Progress:P0}";
        }
        else
        {
            text = "Starting…";
        }
        int others = unfinished.Count - 1;
        if (others > 0)
            text += $" (+{others} more)";

        // NotifyIcon.Text throws above 127 characters.
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    private void ReportBatch(ConversionBatch batch)
    {
        UpdateTrayStatus();

        var jobs = batch.Jobs;
        var succeeded = jobs.Where(j => j.State == JobState.Succeeded).ToList();
        var failed = jobs.Where(j => j.State == JobState.Failed).ToList();
        int cancelled = jobs.Count(j => j.State == JobState.Cancelled);

        foreach (var job in jobs)
        {
            Log.Write(job.State switch
            {
                JobState.Succeeded => $"Converted {job.InputPath} -> {job.OutputPath}",
                JobState.Failed => $"Failed {job.InputPath} -> {job.Target}: {job.Error}",
                _ => $"Cancelled {job.InputPath} -> {job.Target}",
            });
        }

        if (failed.Count > 0)
        {
            string title = failed.Count == jobs.Count ? "Conversion failed" : $"{failed.Count} of {jobs.Count} conversions failed";
            Notify(title, failed[0].Error ?? "Unknown error. Details are in the log.", Forms.ToolTipIcon.Warning);
        }
        else if (succeeded.Count > 0)
        {
            string title = succeeded.Count == 1 ? "Converted" : $"Converted {succeeded.Count} files";
            string body = string.Join(", ", succeeded.Select(j => Path.GetFileName(j.OutputPath)));
            if (cancelled > 0)
                body += $" ({cancelled} cancelled)";
            Notify(title, body, Forms.ToolTipIcon.Info);
        }
        else
        {
            Notify("Conversion cancelled", $"{cancelled} file(s) were not converted.", Forms.ToolTipIcon.Info);
        }
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

        _cancelItem = new Forms.ToolStripMenuItem("Cancel conversions", null, (_, _) =>
        {
            Log.Write("Cancelling all conversions.");
            _queue?.CancelAll();
        })
        {
            Enabled = false,
        };
        menu.Items.Add(_cancelItem);

        menu.Items.Add("Open log", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        return new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = TrayTitle,
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void Notify(string title, string text, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(4000, title, text, icon);

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        // Cancels running conversions; FFmpeg processes are killed and partial files deleted.
        _queue?.Dispose();
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
