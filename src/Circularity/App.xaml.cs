using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

/// <summary>
/// Tray app that wires the gesture hook to the wheel, and the wheel to the dialogs and the
/// conversion queue. No main window: everything happens through the gesture, the tray icon
/// and its notifications.
/// </summary>
public partial class App : Application
{
    // The gesture only arms when one of these apps is in the foreground (the app being dragged
    // from). Games and CAD tools use left+right chords themselves, so they must never be affected.
    private static readonly HashSet<string> AllowedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "olk", "outlook", "thunderbird",
    };

    private const string TrayTitle = "Circularity";

    private Mutex? _singleInstance;
    private Settings _settings = new();
    private GestureHook? _hook;
    private WheelWindow? _wheel;
    private FfmpegConverter? _ffmpeg;
    private WebPdfConverter? _webPdf;
    private ConversionQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _cancelItem;
    private SettingsWindow? _settingsWindow;
    private bool _dialogOpen;

    // Read on the hook thread.
    private volatile bool _paused;
    private volatile bool _rightClickEnabled;
    private volatile bool _shiftEnabled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Circularity", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Circularity is already running (look in the tray).", TrayTitle);
            Shutdown();
            return;
        }

        Log.Write("---- Circularity starting ----");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled exception: {args.Exception}");
            Notify("Something went wrong", args.Exception.Message, Forms.ToolTipIcon.Error);
            args.Handled = true;
        };

        _settings = Settings.Load();

        _ffmpeg = new FfmpegConverter();
        Log.Write(_ffmpeg.FfmpegPath != null
            ? $"Using FFmpeg at {_ffmpeg.FfmpegPath}"
            : "FFmpeg not found; video and audio conversions will fail until it's installed.");

        _webPdf = new WebPdfConverter();
        if (!WebPdfConverter.IsAvailable)
            Log.Write("WebView2 runtime not found; Markdown/HTML/TXT to PDF is unavailable.");

        // Order matters only where two could do the same job: the first that can, does.
        _queue = new ConversionQueue(new IConverter[] { new ImageConverter(), _ffmpeg, new PdfConverter(), _webPdf, new OfficeConverter() });
        _queue.JobChanged += _ => Dispatcher.BeginInvoke(UpdateTrayStatus);
        _queue.BatchFinished += batch => Dispatcher.BeginInvoke(() => ReportBatch(batch));

        _wheel = new WheelWindow { CanHandle = _queue.CanHandle };
        _wheel.ActionChosen += OnActionChosen;

        // With "Same as Windows", follow light/dark changes while running.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _tray = CreateTray();
        ApplySettings(_settings);

        _hook = new GestureHook { ShouldArm = ShouldArm };
        _hook.Triggered += (x, y, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_wheel.IsVisible)
                _wheel.ShowAt(x, y);
        });
        _hook.DragEnded += () => Dispatcher.BeginInvoke(() => _wheel.OnDragEnded());
        Log.Write("Gesture hooks installed.");

        _tray.ShowBalloonTip(4000, "Circularity is running",
            "Drag a file and tap right-click (or Shift), then drop it on a button.", Forms.ToolTipIcon.Info);
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && _settings.Theme == AppTheme.System)
            Dispatcher.BeginInvoke(() => ApplySettings(_settings));
    }

    private void ApplySettings(Settings settings)
    {
        _settings = settings;
        _rightClickEnabled = settings.RightClickTrigger;
        _shiftEnabled = settings.ShiftTrigger;
        _wheel?.ApplySettings(settings);

        if (_tray != null)
        {
            var old = _tray.Icon;
            _tray.Icon = AppIcon.Create(settings.Accent);
            old?.Dispose();
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

    private async void OnActionChosen(string[] files, WheelAction action)
    {
        if (action.Kind == WheelActionKind.Convert)
        {
            _queue!.Enqueue(files, new ConvertTo(action.Target!));
            return;
        }
        if (action.Kind == WheelActionKind.Combine)
        {
            // One job for the whole drop, in the order Explorer gave the files.
            _queue!.Enqueue(new[] { (files[0], (Operation)new CombineIntoPdf(files)) });
            return;
        }

        if (_dialogOpen)
        {
            Notify("Finish the open window first", "A resize, crop or compress window is already open.", Forms.ToolTipIcon.Info);
            return;
        }

        _dialogOpen = true;
        try
        {
            switch (action.Kind)
            {
                case WheelActionKind.Resize:
                    await ResizeAsync(files);
                    break;
                case WheelActionKind.Crop:
                    await CropAsync(files);
                    break;
                case WheelActionKind.Compress:
                    Compress(files);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{action.Label} failed: {ex}");
            Notify($"{action.Label} failed", ex.Message, Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ResizeAsync(string[] files)
    {
        (int, int)? size = null;
        if (files.Length == 1)
        {
            try
            {
                size = Formats.KindOf(files[0]) == FileKind.Video
                    ? await _ffmpeg!.ProbeVideoSizeAsync(files[0])
                    : ImageConverter.ReadSize(files[0]);
            }
            catch (Exception ex)
            {
                // The dialog works without the current size; it just can't show a preview of the result.
                Log.Write($"Couldn't read the size of {files[0]}: {ex.Message}");
            }
        }

        var dialog = Dialog(new ResizeDialog(files, size));
        if (dialog.ShowDialog() == true && dialog.Result != null)
            _queue!.Enqueue(files, new ResizeTo(dialog.Result));
    }

    private void Compress(string[] files)
    {
        var dialog = Dialog(new CompressDialog(files));
        if (dialog.ShowDialog() == true && dialog.Result != null)
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
    }

    private async Task CropAsync(string[] files)
    {
        var items = new List<(string File, Operation Operation)>();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            BitmapSource picture;
            try
            {
                picture = await LoadCropPreviewAsync(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't open {file} for cropping: {ex}");
                Notify("Couldn't open file", $"{Path.GetFileName(file)}: {ex.Message}", Forms.ToolTipIcon.Warning);
                continue;
            }

            var window = Dialog(new CropWindow(picture, Path.GetFileName(file), i + 1, files.Length));
            window.ShowDialog();
            if (window.Outcome == CropOutcome.Cancelled)
                return;
            if (window.Outcome == CropOutcome.Cropped)
                items.Add((file, new CropTo(window.Result)));
        }

        if (items.Count > 0)
            _queue!.Enqueue(items);
    }

    /// <summary>The picture itself, or for a video a frame from about a second in.</summary>
    private async Task<BitmapSource> LoadCropPreviewAsync(string file)
    {
        if (Formats.KindOf(file) != FileKind.Video)
            return await Task.Run(() => ImageConverter.LoadOriented(file));

        string frame = Path.Combine(Path.GetTempPath(), $"Circularity-frame-{Guid.NewGuid():N}.png");
        try
        {
            await _ffmpeg!.ExtractFrameAsync(file, frame);
            return await Task.Run(() => ImageConverter.LoadOriented(frame));
        }
        finally
        {
            File.Delete(frame);
        }
    }

    /// <summary>Gives a window the app icon and the chosen light/dark theme.</summary>
    private T Dialog<T>(T window) where T : Window
    {
        using (var icon = AppIcon.Create(_settings.Accent))
            window.Icon = AppIcon.ToImageSource(icon);
        Theme.ApplyTo(window, _settings.Theme);
        return window;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = Dialog(new SettingsWindow(_settings));
        _settingsWindow.SettingsChanged += settings =>
        {
            ApplySettings(settings);
            settings.Save();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        WindowHelpers.ForceForeground(_settingsWindow);
    }

    /// <summary>Shows what's converting in the tray tooltip and enables "Cancel conversions".</summary>
    private void UpdateTrayStatus()
    {
        if (_tray == null || _queue == null)
            return;

        var unfinished = _queue.Unfinished;
        _cancelItem!.Enabled = unfinished.Count > 0;

        if (unfinished.Count == 0)
        {
            _tray.Text = TrayTitle;
            return;
        }

        var running = unfinished.FirstOrDefault(j => j.State == JobState.Running);
        string text = running != null
            ? $"{Path.GetFileName(running.InputPath)} → {running.Operation.Describe()} {running.Progress:P0}"
            : "Starting…";
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
            string what = job.Operation.Describe();
            Log.Write(job.State switch
            {
                JobState.Succeeded => $"{what}: {job.InputPath} -> {job.OutputPath}",
                JobState.Failed => $"{what} failed for {job.InputPath}: {job.Error}",
                _ => $"{what} cancelled for {job.InputPath}",
            });
        }

        if (failed.Count > 0)
        {
            string title = failed.Count == jobs.Count ? "That didn't work" : $"{failed.Count} of {jobs.Count} files failed";
            Notify(title, failed[0].Error ?? "Unknown error. Details are in the log.", Forms.ToolTipIcon.Warning);
        }
        else if (succeeded.Count > 0)
        {
            string title = succeeded.Count == 1 ? "Done" : $"Done: {succeeded.Count} files";
            string body = string.Join(", ", succeeded.Select(j => j.Operation is CompressTo
                ? $"{Path.GetFileName(j.OutputPath)} ({FileSizes.Format(SizeOf(j.InputPath))} → {FileSizes.Format(SizeOf(j.OutputPath!))})"
                : Path.GetFileName(j.OutputPath)));
            if (cancelled > 0)
                body += $" ({cancelled} cancelled)";
            Notify(title, body, Forms.ToolTipIcon.Info);
        }
        else
        {
            Notify("Cancelled", $"{cancelled} file(s) were not converted.", Forms.ToolTipIcon.Info);
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());

        var pause = new Forms.ToolStripMenuItem("Pause gestures") { CheckOnClick = true };
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

        var tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(_settings.Accent),
            Text = TrayTitle,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        return tray;
    }

    private void Notify(string title, string text, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, icon);

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        // Cancels running conversions; FFmpeg processes are killed and partial files deleted.
        _queue?.Dispose();
        _webPdf?.Dispose();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        Log.Write("---- Circularity exiting ----");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
