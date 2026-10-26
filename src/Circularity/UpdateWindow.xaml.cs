using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;

namespace Circularity;

/// <summary>Checks for a newer release when opened, and offers to download and install it.</summary>
internal partial class UpdateWindow : Window
{
    private readonly UpdateChecker _checker = new();
    private readonly CancellationTokenSource _cancel = new();
    private UpdateInfo? _update;

    /// <summary>Raised just before the installer starts, so the app can get out of its way.</summary>
    public event Action? InstallerStarted;

    public UpdateWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            WindowHelpers.ForceForeground(this);
            await CheckAsync();
        };
        Closed += (_, _) => _cancel.Cancel();
    }

    private async System.Threading.Tasks.Task CheckAsync()
    {
        try
        {
            _update = await _checker.GetLatestAsync(_cancel.Token);
            Progress.Visibility = Visibility.Collapsed;
            if (_update == null)
            {
                Heading.Text = "No releases yet";
                Detail.Text = $"You have Circularity {Diagnostics.AppVersion()}. Nothing has been published on GitHub yet.";
                return;
            }

            ReleasePageButton.Visibility = Visibility.Visible;
            if (!_update.IsNewer)
            {
                Heading.Text = "You're up to date";
                Detail.Text = $"Circularity {Diagnostics.AppVersion()} is the latest version.";
                return;
            }

            Heading.Text = $"Circularity {_update.Tag.TrimStart('v')} is available";
            Detail.Text = $"You have {Diagnostics.AppVersion()}.";
            if (_update.Notes.Length > 0)
            {
                Notes.Text = _update.Notes;
                NotesBox.Visibility = Visibility.Visible;
            }
            InstallButton.Visibility = _update.InstallerUrl != null ? Visibility.Visible : Visibility.Collapsed;
            if (_update.InstallerUrl == null)
                Detail.Text += " This release has no installer attached; get it from GitHub.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (_cancel.IsCancellationRequested)
                return;
            Log.Write($"Update check failed: {ex.Message}");
            Progress.Visibility = Visibility.Collapsed;
            Heading.Text = "Couldn't check for updates";
            Detail.Text = ex is HttpRequestException ? "Check your internet connection and try again." : ex.Message;
            ReleasePageButton.Visibility = Visibility.Visible;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null)
            return;
        InstallButton.IsEnabled = false;
        Heading.Text = $"Downloading Circularity {_update.Tag.TrimStart('v')}…";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        try
        {
            var progress = new Progress<double>(value => Progress.Value = value * 100);
            string installer = await _checker.DownloadInstallerAsync(_update, progress, _cancel.Token);
            Log.Write($"Installing update {_update.Tag.TrimStart('v')} from {installer}.");
            Heading.Text = "Installing…";
            UpdateChecker.StartInstaller(installer);
            InstallerStarted?.Invoke();
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"Update failed: {ex.Message}");
            Heading.Text = "The update didn't work";
            Detail.Text = ex.Message;
            Progress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-download.
        }
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_update?.PageUrl ?? UpdateChecker.ReleasesPage) { UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
