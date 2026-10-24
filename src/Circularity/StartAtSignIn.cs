using System;
using Microsoft.Win32;

namespace Circularity;

/// <summary>
/// "Start Circularity when I sign in": the per-user Run entry in the registry. It uses the same entry
/// the installer creates (and its uninstaller removes), so the installer's checkbox and this toggle
/// always agree.
/// </summary>
internal static class StartAtSignIn
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager's Startup apps page records "Disabled" without deleting the Run entry.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Circularity";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
                return false;

            // Task Manager writes a flag whose first byte is odd (e.g. 03) for disabled, even (02) for enabled.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] flags || flags.Length == 0 || (flags[0] & 1) == 0;
        }
    }

    /// <param name="exePath">The exe to start; defaults to this one.</param>
    public static void Set(bool enabled, string? exePath = null)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string path = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Circularity is installed.");
            run.SetValue(ValueName, $"\"{path}\"");
            // If it was disabled in Task Manager, turning it on here should actually turn it on.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
