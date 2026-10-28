using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    private bool _isMainInstance; // False for a copy that only handed its files to the running one.
    private Settings _settings = new();
    private GestureHook? _hook;
    private WheelWindow? _wheel;
    private FfmpegConverter? _ffmpeg;
    private WebPdfConverter? _webPdf;
    private ConversionQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _cancelItem;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private WelcomeWindow? _welcomeWindow;
    private TutorialWindow? _tutorialWindow;
    private InstanceChannel? _channel;
    private GlobalHotkey? _hotkey;
    private bool _dialogOpen;

    // Background update checks: one shortly after start, then one every interval. Settings apply on
    // every click, so the timers are only rebuilt when the update options themselves change.
    private System.Windows.Threading.DispatcherTimer? _firstUpdateCheck;
    private System.Windows.Threading.DispatcherTimer? _updateCheckTimer;
    private (bool Enabled, int Hours)? _updateSchedule;
    private bool _checkingForUpdates;

    // Explorer launches one copy per selected file for "Convert with Circularity", each handing its
    // file over separately, so files arriving close together are gathered into one wheel.
    private readonly List<string> _incomingFiles = new();
    private System.Windows.Threading.DispatcherTimer? _incomingTimer;

    /// <summary>What clicking the latest notification opens: a converted file or folder, or nothing.</summary>
    private string? _notificationTarget;

    /// <summary>Run instead of opening a file when the latest notification is clicked (an update, say).</summary>
    private Action? _notificationAction;

    // Read on the hook thread.
    private volatile bool _paused;
    private volatile bool _rightClickEnabled;
    private volatile bool _shiftEnabled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var launchFiles = e.Args.Where(File.Exists).ToArray();
        _singleInstance = new Mutex(true, @"Local\Circularity", out _isMainInstance);
        if (!_isMainInstance)
        {
            // Already running: hand over the files, or ask it to show Settings, and quietly exit.
            var message = launchFiles.Length > 0 ? launchFiles : new[] { InstanceChannel.OpenSettingsMessage };
            if (!InstanceChannel.TrySend(message))
                MessageBox.Show("Circularity is already running (look in the tray).", TrayTitle);
            Shutdown();
            return;
        }

        Log.Write("---- Circularity starting ----");

        // Draw with the CPU rather than the graphics card. That keeps the graphics driver's memory out
        // of the process: about 20 MB instead of about 43 MB for as long as Circularity sits in the
        // tray. The price is more CPU while the wheel is on screen (roughly 1.3 s instead of 0.7 s
        // over a typical use), which is a second or two at a time; idle, both use none.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
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
        _queue = new ConversionQueue(new IConverter[] { new ImageConverter(_ffmpeg), _ffmpeg, new PdfConverter(), _webPdf, new OfficeConverter() });
        _queue.JobChanged += _ => Dispatcher.BeginInvoke(UpdateTrayStatus);
        _queue.BatchFinished += batch => Dispatcher.BeginInvoke(() => ReportBatch(batch));

        _wheel = new WheelWindow { CanHandle = _queue.CanHandle };
        _wheel.ActionChosen += OnActionChosen;

        // With "Same as Windows", follow light/dark changes while running.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _tray = CreateTray();
        ApplySettings(_settings);
        StartAutoUpdateCheck();

        _hook = new GestureHook { ShouldArm = ShouldArm };
        _hook.Triggered += (x, y, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_wheel.IsOpen)
                _wheel.ShowAt(x, y);
        });
        _hook.DragEnded += () => Dispatcher.BeginInvoke(() => _wheel.OnDragEnded());
        Log.Write("Gesture hooks installed.");

        _channel = new InstanceChannel();
        _channel.Received += lines => Dispatcher.BeginInvoke(() => OnLaunchedAgain(lines));
        _channel.StartListening();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += OnHotkey;
        UpdateHotkey();

        if (launchFiles.Length > 0)
        {
            // Started by "Convert with Circularity" or Send to: go straight to the wheel.
            ReceiveFiles(launchFiles);
        }
        else if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            ShowWelcome();
        }
        else if (_settings.NotifyOnStart)
        {
            Notify("Circularity is running",
                "Drag a file and tap right-click (or Shift), or select files and press Ctrl+Alt+C.", Forms.ToolTipIcon.Info);
        }
    }

    private void ShowWelcome()
    {
        if (_welcomeWindow != null)
        {
            _welcomeWindow.Activate();
            return;
        }
        _welcomeWindow = Dialog(new WelcomeWindow(_settings));
        _welcomeWindow.OpenSettingsRequested += OpenSettings;
        _welcomeWindow.TutorialRequested += OpenTutorial;
        _welcomeWindow.Closed += (_, _) =>
        {
            _welcomeWindow = null;
            ScheduleMemoryRelease();
        };
        _welcomeWindow.Show();
    }

    /// <summary>
    /// Puts the diagnostics on the clipboard and opens a new GitHub issue with a short template to
    /// paste them into. They hold folder paths (which can include the user's name), so they go only
    /// on the clipboard, for the user to look over, and never into the web address.
    /// </summary>
    private async void ReportBug()
    {
        string? report = null;
        try
        {
            report = await Task.Run(async () => Diagnostics.Report(await Diagnostics.CollectAsync(_ffmpeg)));
        }
        catch (Exception ex)
        {
            // Never let a failed report take the app down; the issue page still opens.
            Log.Write($"Couldn't collect diagnostics for a bug report: {ex.Message}");
        }
        // Back on the app's thread, which the clipboard and the tray need, whichever thread the await resumed on.
        Dispatcher.Invoke(() => OpenBugReport(report));
    }

    private void OpenBugReport(string? report)
    {
        bool copied = false;
        if (report != null)
        {
            try
            {
                Clipboard.SetText(report);
                copied = true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Write($"Couldn't copy diagnostics (clipboard busy): {ex.Message}");
            }
        }

        string body =
            "**What happened?**\n\n\n" +
            "**What did you expect to happen?**\n\n\n" +
            "**How to make it happen again**\n1. \n2. \n\n" +
            "**Diagnostics**\n" +
            (copied
                ? "Circularity copied these to your clipboard: click below and press Ctrl+V. They include folder paths from your PC, so remove anything you'd rather not share.\n\n"
                : "Open Settings → About → Copy diagnostics, then paste them here.\n\n") +
            "```\n\n```\n";
        string url = $"https://github.com/{UpdateChecker.Repository}/issues/new?body={Uri.EscapeDataString(body)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (copied)
                Notify("Diagnostics copied", "Paste them into the bug report that just opened in your browser (Ctrl+V).", Forms.ToolTipIcon.Info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"Couldn't open the bug report page: {ex.Message}");
            Notify("Couldn't open your browser", $"Report bugs at github.com/{UpdateChecker.Repository}/issues.", Forms.ToolTipIcon.Warning);
        }
    }

    private void OpenTutorial()
    {
        if (_tutorialWindow != null)
        {
            _tutorialWindow.Activate();
            return;
        }
        _tutorialWindow = Dialog(new TutorialWindow(_settings, _queue!.CanHandle));
        _tutorialWindow.OpenSettingsRequested += OpenSettings;
        _tutorialWindow.Closed += (_, _) =>
        {
            _tutorialWindow = null;
            ScheduleMemoryRelease();
        };
        _tutorialWindow.Show();
    }

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

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General)
            return;
        if (_settings.Theme == AppTheme.System)
            Dispatcher.BeginInvoke(() => ApplySettings(_settings));
        else
            Dispatcher.BeginInvoke(UpdateTrayIcon); // The taskbar may have switched between light and dark.
    }

    private void ApplySettings(Settings settings)
    {
        _settings = settings;
        _rightClickEnabled = settings.RightClickTrigger;
        _shiftEnabled = settings.ShiftTrigger;
        _wheel?.ApplySettings(settings);
        UpdateHotkey();
        UpdateTrayIcon();
    }

    /// <summary>The icon in the accent colour, in the version that stands out on the taskbar: white on dark, charcoal on light.</summary>
    private void UpdateTrayIcon()
    {
        if (_tray == null)
            return;
        var old = _tray.Icon;
        _tray.Icon = AppIcon.Create(_settings.Accent, darkDisc: !Theme.TaskbarIsDark());
        old?.Dispose();
    }

    /// <summary>(Re)starts the background update checks if their settings changed.</summary>
    private void StartAutoUpdateCheck()
    {
        int hours = Settings.ClampUpdateInterval(_settings.AutoCheckIntervalHours);
        var schedule = (_settings.AutoCheckForUpdates, hours);
        if (_updateSchedule == schedule)
            return;
        bool firstTime = _updateSchedule == null;
        _updateSchedule = schedule;

        _updateCheckTimer?.Stop();
        _updateCheckTimer = null;
        if (!_settings.AutoCheckForUpdates)
        {
            _firstUpdateCheck?.Stop();
            _firstUpdateCheck = null;
            return;
        }

        // 0 means "On startup" only.
        if (hours > 0)
        {
            _updateCheckTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(hours) };
            _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesSilentAsync();
            _updateCheckTimer.Start();
        }

        // Once shortly after start (or after turning it on), not again for every interval change.
        if (firstTime || _firstUpdateCheck == null)
        {
            _firstUpdateCheck?.Stop();
            _firstUpdateCheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _firstUpdateCheck.Tick += async (_, _) =>
            {
                _firstUpdateCheck?.Stop();
                await CheckForUpdatesSilentAsync();
            };
            _firstUpdateCheck.Start();
        }
    }

    /// <summary>Checks for an update without any window, and mentions each new version only once.</summary>
    private async Task CheckForUpdatesSilentAsync()
    {
        if (_checkingForUpdates || !_settings.AutoCheckForUpdates)
            return;
        _checkingForUpdates = true;
        try
        {
            var update = await new UpdateChecker().GetLatestAsync();
            if (update == null || !update.IsNewer || update.Tag == _settings.LastNotifiedUpdate)
                return;

            _settings.LastNotifiedUpdate = update.Tag;
            _settings.Save();
            Notify($"Circularity {update.Tag.TrimStart('v')} is available",
                $"You have {Diagnostics.AppVersion()}. Click to see what's new and update.",
                Forms.ToolTipIcon.Info,
                onClick: OpenUpdates);
        }
        catch (Exception ex)
        {
            // Runs from a timer, so nothing may escape: an unhandled exception here would close the app.
            Log.Write($"Background update check failed: {ex.Message}");
        }
        finally
        {
            _checkingForUpdates = false;
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
        if (action.Preset is { } preset)
        {
            _queue!.Enqueue(files, preset.Operation);
            return;
        }

        if (_dialogOpen)
        {
            Notify("Finish the open window first", "A resize, crop, compress or trim window is already open.", Forms.ToolTipIcon.Info);
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
                case WheelActionKind.Compress when Formats.KindOf(files[0]) == FileKind.Video:
                    await CompressVideoAsync(files);
                    break;
                case WheelActionKind.Compress when Formats.KindOf(files[0]) == FileKind.Document:
                    CompressPdf(files);
                    break;
                case WheelActionKind.Compress:
                    Compress(files);
                    break;
                case WheelActionKind.Trim:
                    await TrimAsync(files);
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
            // The crop window holds the whole picture; let it go even if nothing was converted.
            ScheduleMemoryRelease();
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
        {
            _queue!.Enqueue(files, new ResizeTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Resize: dialog.Result));
        }
    }

    private void Compress(string[] files)
    {
        var dialog = Dialog(new CompressDialog(files));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Image)));
        }
    }

    private async Task CompressVideoAsync(string[] files)
    {
        MediaInfo? info = null;
        if (files.Length == 1)
        {
            try
            {
                info = await _ffmpeg!.ProbeAsync(files[0]);
            }
            catch (Exception ex)
            {
                // The dialog works without it; it just can't estimate the result's size.
                Log.Write($"Couldn't read {files[0]}: {ex.Message}");
            }
        }
        var dialog = Dialog(new VideoCompressDialog(files, info));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Video)));
        }
    }

    private void CompressPdf(string[] files)
    {
        var dialog = Dialog(new PdfCompressDialog(files));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Document)));
        }
    }

    /// <summary>One trim window per file, like cropping; cancelling one stops the rest.</summary>
    private async Task TrimAsync(string[] files)
    {
        var items = new List<(string File, Operation Operation)>();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            MediaInfo info;
            try
            {
                info = await _ffmpeg!.ProbeAsync(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't read {file} for trimming: {ex}");
                Notify("Couldn't open file", $"{Path.GetFileName(file)}: {ex.Message}", Forms.ToolTipIcon.Warning);
                continue;
            }
            if (info.Duration <= TimeSpan.FromSeconds(0.2))
            {
                Notify("Nothing to trim", $"{Path.GetFileName(file)} is too short to trim.", Forms.ToolTipIcon.Info);
                continue;
            }

            Func<TimeSpan, Task<System.Windows.Media.ImageSource?>>? frameAt = info.HasVideo ? at => FrameAtAsync(file, at) : null;
            var window = Dialog(new TrimDialog(file, info.Duration, files.Length > 1 ? $"{i + 1} of {files.Length}" : null, frameAt));
            if (window.ShowDialog() != true || window.Result == null)
                return;
            items.Add((file, window.Result));
        }
        if (items.Count > 0)
            _queue!.Enqueue(items);
    }

    /// <summary>A small picture of the frame at a time, for the trim window; null if there isn't one.</summary>
    private async Task<System.Windows.Media.ImageSource?> FrameAtAsync(string file, TimeSpan at)
    {
        string png = Path.Combine(Path.GetTempPath(), $"Circularity-frame-{Guid.NewGuid():N}.png");
        try
        {
            if (!await _ffmpeg!.ExtractFrameAtAsync(file, at, png, 480))
                return null;
            return await Task.Run(() => ImageConverter.LoadOriented(png));
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't show the frame at {at} of {file}: {ex.Message}");
            return null;
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>Saves a new wheel button; it goes at the end of the order, so usually behind More until moved.</summary>
    private void AddPreset(Preset preset)
    {
        var settings = _settings.Clone();
        settings.Presets.Add(preset);
        settings.Save();
        ApplySettings(settings);
        _settingsWindow?.Reload(settings);
        Log.Write($"Added a wheel button '{preset.Name}' ({preset.Operation.Describe()}).");
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
        // The white version, like the exe's: the same icon also shows on the window's taskbar button.
        using (var icon = AppIcon.Create(_settings.Accent, darkDisc: false))
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

        _settingsWindow = Dialog(new SettingsWindow(_settings, _queue!.CanHandle, _ffmpeg));
        _settingsWindow.SettingsChanged += settings =>
        {
            // Kept by the app, not shown in Settings, so the window's copy may be out of date.
            settings.LastNotifiedUpdate = _settings.LastNotifiedUpdate;
            ApplySettings(settings);
            settings.Save();
            StartAutoUpdateCheck();
        };
        _settingsWindow.CheckForUpdatesRequested += OpenUpdates;
        _settingsWindow.TutorialRequested += OpenTutorial;
        _settingsWindow.WelcomeRequested += ShowWelcome;
        _settingsWindow.BugReportRequested += ReportBug;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleMemoryRelease();
        };
        _settingsWindow.Show();
        WindowHelpers.ForceForeground(_settingsWindow);
    }

    private void OpenUpdates()
    {
        if (_updateWindow != null)
        {
            _updateWindow.Activate();
            return;
        }
        _updateWindow = Dialog(new UpdateWindow());
        // The installer replaces this copy's files and starts the new version afterwards.
        _updateWindow.InstallerStarted += () => Shutdown();
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
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
        ScheduleMemoryRelease();

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
        else if (!_settings.NotifyOnFinish)
        {
            // Finished fine and the user has turned these off; failures above are always shown.
        }
        else if (succeeded.Count > 0)
        {
            string title = succeeded.Count == 1 ? "Done" : $"Done: {succeeded.Count} files";
            string body = string.Join(", ", succeeded.Select(j => j.Operation is CompressTo
                ? $"{Path.GetFileName(j.OutputPath)} ({FileSizes.Format(SizeOf(j.InputPath))} → {FileSizes.Format(SizeOf(j.OutputPath!))})"
                : Path.GetFileName(j.OutputPath)));
            if (cancelled > 0)
                body += $" ({cancelled} cancelled)";

            // Copy output file(s) to clipboard if enabled
            if (_settings.CopyToClipboardOnFinish)
            {
                try
                {
                    var filePaths = succeeded
                        .Where(j => !string.IsNullOrEmpty(j.OutputPath) && File.Exists(j.OutputPath!))
                        .Select(j => j.OutputPath!)
                        .ToArray();
                    if (filePaths.Length > 0)
                    {
                        var paths = new StringCollection();
                        paths.AddRange(filePaths);
                        Clipboard.SetFileDropList(paths);
                        body += filePaths.Length == 1 ? "\nFile copied to clipboard." : "\nFiles copied to clipboard.";
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Clipboard busy, ignore silently
                }
            }

            Notify(title, body + "\nClick to show.", Forms.ToolTipIcon.Info, openOnClick: succeeded[0].OutputPath);
        }
        else
        {
            Notify("Cancelled", $"{cancelled} file(s) were not converted.", Forms.ToolTipIcon.Info);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _releaseTimer;

    /// <summary>
    /// A few seconds after the last conversion (or a window) is done, gives the memory it used back
    /// to Windows: a 12-megapixel photo alone is about 48 MB of pixels. Left to itself, .NET keeps
    /// freed memory reserved for next time, so an idle Circularity would stay at its peak size.
    /// One full, compacting collection is cheap at that point, since nothing else is going on.
    /// </summary>
    private void ScheduleMemoryRelease()
    {
        if (_releaseTimer == null)
        {
            _releaseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _releaseTimer.Tick += (_, _) =>
            {
                _releaseTimer.Stop();
                // Try again later if something started in the meantime.
                if (_queue?.Unfinished.Count > 0 || _dialogOpen)
                    return;
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            };
        }
        _releaseTimer.Stop();
        _releaseTimer.Start();
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
        menu.Items.Add("Check for updates…", null, (_, _) => OpenUpdates());

        var help = new Forms.ToolStripMenuItem("Help");
        help.DropDownItems.Add("Tutorial…", null, (_, _) => OpenTutorial());
        help.DropDownItems.Add("Welcome guide…", null, (_, _) => ShowWelcome());
        help.DropDownItems.Add(new Forms.ToolStripSeparator());
        help.DropDownItems.Add("Report a bug…", null, (_, _) => ReportBug());
        menu.Items.Add(help);

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
            Icon = AppIcon.Create(_settings.Accent, darkDisc: !Theme.TaskbarIsDark()),
            Text = TrayTitle,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        tray.BalloonTipClicked += (_, _) => OnNotificationClicked();
        return tray;
    }

    /// <param name="openOnClick">A file (shown selected in Explorer) or folder (opened) for when the notification is clicked.</param>
    /// <param name="onClick">Run when the notification is clicked, instead of opening a file.</param>
    private void Notify(string title, string text, Forms.ToolTipIcon icon, string? openOnClick = null, Action? onClick = null)
    {
        _notificationTarget = openOnClick;
        _notificationAction = onClick;
        _tray?.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, icon);
    }

    private void OnNotificationClicked()
    {
        string? target = _notificationTarget;
        var action = _notificationAction;
        _notificationTarget = null;
        _notificationAction = null;
        if (action != null)
        {
            action();
            return;
        }
        try
        {
            if (Directory.Exists(target))
                Process.Start("explorer.exe", $"\"{target}\"");
            else if (File.Exists(target))
                Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"Couldn't show {target}: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _hotkey?.Dispose();
        _channel?.Dispose();
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
        if (_isMainInstance)
            Log.Write("---- Circularity exiting ----");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
