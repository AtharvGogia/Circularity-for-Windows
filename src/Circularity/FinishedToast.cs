using System;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Circularity;

/// <summary>
/// The "Done" notification as a real Windows notification, with buttons: Open (the file, in its
/// usual app) and Open folder (showing it selected in File Explorer, as clicking the notification
/// does). The tray's balloon notifications can't have buttons. The toolkit registers Circularity with
/// Windows for this under HKCU; <see cref="Unregister"/> takes that out again on uninstall.
/// </summary>
internal static class FinishedToast
{
    public const string Open = "open";
    public const string OpenFolder = "folder";

    private const string ActionKey = "action";
    private const string PathKey = "path";

    /// <summary>The argument Windows starts the app with when a notification is clicked while it isn't running.</summary>
    public const string ActivatedArgument = "-ToastActivated";

    /// <summary>
    /// Listens for clicks. <paramref name="onAction"/> gets the action (<see cref="Open"/> or
    /// <see cref="OpenFolder"/>) and the path, on a background thread.
    /// </summary>
    public static void Listen(Action<string, string> onAction)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue(ActionKey, out string? action) && args.TryGetValue(PathKey, out string? path))
                    onAction(action, path);
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Notifications with buttons are unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the notification; false if Windows wouldn't, so the caller can fall back to a balloon.
    /// </summary>
    /// <param name="path">The result (the first one, for several); a folder for PDF pages.</param>
    /// <param name="single">One file, so it can be opened; several only get Open folder.</param>
    public static bool TryShow(string title, string body, string path, bool single)
    {
        try
        {
            var builder = new ToastContentBuilder()
                // Clicking the notification itself does what it always did: show the result in File Explorer.
                .AddArgument(ActionKey, OpenFolder)
                .AddArgument(PathKey, path)
                .AddText(title)
                .AddText(body);
            if (single && File.Exists(path))
                builder.AddButton(Button("Open", Open, path));
            builder.AddButton(Button("Open folder", OpenFolder, path));
            builder.Show();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't show the notification with buttons ({ex.Message}); using the tray's instead.");
            return false;
        }
    }

    /// <summary>Removes what the toolkit registered with Windows; run by the uninstaller.</summary>
    public static void Unregister()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't unregister notifications: {ex.Message}");
        }
    }

    private static ToastButton Button(string label, string action, string path) =>
        new ToastButton().SetContent(label).AddArgument(ActionKey, action).AddArgument(PathKey, path);
}
