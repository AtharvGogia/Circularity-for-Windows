using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
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

    /// <summary>Passed by the uninstaller (see Circularity.iss) to unregister the notifications.</summary>
    public const string UninstallCleanupArgument = "--uninstall-cleanup";

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
    private Forms.ToolStripMenuItem? _undoItem;
    private readonly UndoLastConversion _undo = new();
    private FolderWatcher? _folderWatcher;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private WelcomeWindow? _welcomeWindow;
    private TutorialWindow? _tutorialWindow;
    private InstanceChannel? _channel;
    private GlobalHotkey? _hotkey;
    private bool _dialogOpen;

    // Read on the hook thread.
    private volatile bool _paused;
    private volatile bool _rightClickEnabled;
    private volatile bool _shiftEnabled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Run by the uninstaller: take out what Windows was told about the notifications, then go.
        if (e.Args.Contains(UninstallCleanupArgument))
        {
            FinishedToast.Unregister();
            Shutdown();
            return;
        }

        var launchFiles = e.Args.Where(File.Exists).ToArray();
        _singleInstance = new Mutex(true, @"Local\Circularity", out _isMainInstance);
        if (!_isMainInstance && e.Args.Contains(CrashGuard.RestartedArgument))
        {
            // Started by a copy that just crashed: wait for it to finish exiting, then take over.
            try
            {
                _isMainInstance = _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                _isMainInstance = true; // It died holding the lock, which now belongs to this copy.
            }
        }
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
        CrashGuard.Install(this,
            recovered: crash => Notify("Something went wrong",
                $"{crash.Message}\nCircularity carried on. Click to report it.", Forms.ToolTipIcon.Error, onClick: () => ReportBug(crash)),
            // Otherwise the icon stays in the tray, looking alive, until the mouse passes over it.
            dying: () => _tray?.Dispose());

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

        // Started (and changed) by ApplySettings, from Settings > Features > Watched folders.
        _folderWatcher = new FolderWatcher(OutputPaths.IsProduced);
        _folderWatcher.FilesArrived += (folder, files) => Dispatcher.BeginInvoke(() => OnWatchedFilesArrived(folder, files));

        _wheel = new WheelWindow { CanHandle = _queue.CanHandle };
        _wheel.ActionChosen += OnActionChosen;

        // With "Same as Windows", follow light/dark changes while running.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _tray = CreateTray();
        ApplySettings(_settings);
        StartAutoUpdateCheck();
        StartUsagePing();

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

        // Clicks on the "Done" notification's buttons arrive on a background thread.
        FinishedToast.Listen((action, path) => Dispatcher.BeginInvoke(() => OnFinishedToastAction(action, path)));

        var crashed = CrashGuard.TakePending();
        if (launchFiles.Length > 0)
        {
            // Started by "Convert with Circularity" or Send to: go straight to the wheel.
            ReceiveFiles(launchFiles);
        }
        else if (crashed != null)
        {
            bool restarted = e.Args.Contains(CrashGuard.RestartedArgument);
            Notify("Circularity closed unexpectedly",
                (restarted ? "It started again by itself. " : "") + "Click to report what happened, so it can be fixed.",
                Forms.ToolTipIcon.Warning, onClick: () => ReportBug(crashed));
        }
        else if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            ShowWelcome();
        }
        else if (_settings.NotifyOnStart && !e.Args.Contains(FinishedToast.ActivatedArgument))
        {
            // (Not when started by clicking an old notification: the click itself is the point.)
            Notify("Circularity is running",
                "Drag a file and tap right-click (or Shift), or select files and press Ctrl+Alt+C.", Forms.ToolTipIcon.Info);
        }

        CrashTestHook(e.Args);
    }

    /// <summary>
    /// For EngineSmokeTest --crash-test only: fails on purpose shortly after starting, on this thread
    /// ("ui") or another ("background"), to check the safety net. Never in the copy restarted after a
    /// crash, so the test can't loop.
    /// </summary>
    private void CrashTestHook(string[] args)
    {
        string? mode = Environment.GetEnvironmentVariable("CIRCULARITY_TEST_CRASH");
        if (mode == null || args.Contains(CrashGuard.RestartedArgument))
            return;
        CrashGuard.RestartAfterUptime = TimeSpan.Zero;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (mode == "background")
                new Thread(() => throw new InvalidOperationException("Crash test (background thread)")).Start();
            else
                throw new InvalidOperationException("Crash test (app thread)");
        };
        timer.Start();
    }

    private void ApplySettings(Settings settings)
    {
        _settings = settings;
        _rightClickEnabled = settings.RightClickTrigger;
        _shiftEnabled = settings.ShiftTrigger;
        _wheel?.ApplySettings(settings);
        Engine.OutputPaths.SaveTo = settings.OutputFolder;
        _folderWatcher?.Update(settings.WatchedFolders);
        UpdateHotkey();
        UpdateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _hotkey?.Dispose();
        _channel?.Dispose();
        // Cancels running conversions; FFmpeg processes are killed and partial files deleted.
        _folderWatcher?.Dispose();
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
