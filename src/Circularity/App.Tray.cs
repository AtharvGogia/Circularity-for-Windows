using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// The tray icon, its menu and status, and notifications.
public partial class App
{
    /// <summary>What clicking the latest notification opens: a converted file or folder, or nothing.</summary>
    private string? _notificationTarget;

    /// <summary>Run instead of opening a file when the latest notification is clicked (an update, say).</summary>
    private Action? _notificationAction;

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General)
            return;
        if (_settings.Theme == AppTheme.System)
            Dispatcher.BeginInvoke(() => ApplySettings(_settings));
        else
            Dispatcher.BeginInvoke(UpdateTrayIcon); // The taskbar may have switched between light and dark.
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

        _undoItem = new Forms.ToolStripMenuItem("Undo last conversion", null, (_, _) => UndoLast()) { Enabled = false };
        menu.Items.Add(_undoItem);

        menu.Items.Add("Open log", null, (_, _) =>
        {
            Log.Flush();
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        });
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
        ShowInExplorer(target);
    }
}
