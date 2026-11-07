using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// Settings, updates, welcome, tutorial and bug-report windows.
public partial class App
{
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
            bool share = _welcomeWindow.ShareUsageCount;
            _welcomeWindow = null;
            _settings.SetShareUsageCount(share);
            _settings.Save();
            // The first attempt waited for this window to close, so it starts afresh.
            _usageTimersOn = null;
            StartUsagePing();
            ScheduleMemoryRelease();
        };
        _welcomeWindow.Show();
    }

    /// <summary>
    /// Puts the diagnostics on the clipboard and opens a new GitHub issue with a short template to
    /// paste them into. They hold folder paths (which can include the user's name), so they go only
    /// on the clipboard, for the user to look over, and never into the web address.
    /// </summary>
    /// <param name="crash">A crash to report: its details go first on the clipboard, and its kind (not its message, which can hold paths) in the title.</param>
    private async void ReportBug(CrashReport? crash = null)
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
        if (crash != null)
            report = crash.Text + Environment.NewLine + (report ?? "");
        // Back on the app's thread, which the clipboard and the tray need, whichever thread the await resumed on.
        Dispatcher.Invoke(() => OpenBugReport(report, crash));
    }

    private void OpenBugReport(string? report, CrashReport? crash)
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
        if (crash != null)
            url += "&title=" + Uri.EscapeDataString($"Crash: {crash.Kind}");
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
            settings.UseCounts = _settings.UseCounts;
            if (settings.ShareUsageCount && _settings.ShareUsageCount)
            {
                settings.UsageId = _settings.UsageId;
                settings.LastUsagePing = _settings.LastUsagePing;
            }
            ApplySettings(settings);
            settings.Save();
            StartAutoUpdateCheck();
            StartUsagePing();
        };
        _settingsWindow.CheckForUpdatesRequested += OpenUpdates;
        _settingsWindow.TutorialRequested += OpenTutorial;
        _settingsWindow.WelcomeRequested += ShowWelcome;
        _settingsWindow.BugReportRequested += () => ReportBug();
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
}
