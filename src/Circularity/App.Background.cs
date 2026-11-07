using System;
using System.Threading.Tasks;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// Background update checks and the anonymous daily count.
public partial class App
{
    // Background update checks: one shortly after start, then one every interval. Settings apply on
    // every click, so the timers are only rebuilt when the update options themselves change.
    private System.Windows.Threading.DispatcherTimer? _firstUpdateCheck;
    private System.Windows.Threading.DispatcherTimer? _updateCheckTimer;
    private (bool Enabled, int Hours)? _updateSchedule;
    private bool _checkingForUpdates;

    // The anonymous daily count: one attempt shortly after start, then every few hours until one goes
    // through (it's sent at most once a day, whenever the date has changed).
    private System.Windows.Threading.DispatcherTimer? _firstUsagePing;
    private System.Windows.Threading.DispatcherTimer? _usagePingTimer;
    private bool _pingingUsage;
    private bool? _usageTimersOn;

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

    /// <summary>(Re)starts the anonymous daily count's timers when it's turned on or off.</summary>
    private void StartUsagePing()
    {
        if (_usageTimersOn == _settings.ShareUsageCount)
            return;
        _usageTimersOn = _settings.ShareUsageCount;

        _firstUsagePing?.Stop();
        _firstUsagePing = null;
        _usagePingTimer?.Stop();
        _usagePingTimer = null;
        if (!_settings.ShareUsageCount)
            return;

        _usagePingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _usagePingTimer.Tick += async (_, _) => await PingUsageAsync();
        _usagePingTimer.Start();

        _firstUsagePing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _firstUsagePing.Tick += async (_, _) =>
        {
            _firstUsagePing?.Stop();
            await PingUsageAsync();
        };
        _firstUsagePing.Start();
    }

    /// <summary>Sends the anonymous count if it hasn't gone today. Never throws: it runs from a timer.</summary>
    private async Task PingUsageAsync()
    {
        // Not while the welcome window is open: its checkbox may still be about to be unticked.
        if (_pingingUsage || _welcomeWindow != null)
            return;
        _pingingUsage = true;
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!_settings.ShareUsageCount || _settings.UsageId is not { } id || !UsagePing.IsDue(_settings.LastUsagePing, today))
                return;
            if (!await new UsagePing().SendAsync(id))
                return;
            // Settings may have been changed (or the count turned off) while it was being sent.
            if (_settings.ShareUsageCount && _settings.UsageId == id)
            {
                _settings.LastUsagePing = UsagePing.FormatDate(today);
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Usage count failed: {ex.Message}");
        }
        finally
        {
            _pingingUsage = false;
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
}
