using System.Windows;
using Microsoft.Win32;

namespace Circularity;

internal enum AppTheme { System, Light, Dark }

internal static class Theme
{
    /// <summary>Whether to draw dark, resolving "System" from the Windows app colour setting.</summary>
    public static bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => SystemUsesDark(),
    };

    /// <summary>Gives a dialog the Windows 11 look in the chosen theme.</summary>
    public static void ApplyTo(Window window, AppTheme theme)
    {
        window.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    /// <summary>Settings → Personalization → Colors → "Choose your default Windows mode": the taskbar's colour.</summary>
    public static bool TaskbarIsDark() => ReadsDark("SystemUsesLightTheme");

    /// <summary>Settings → Personalization → Colors → "Choose your default app mode".</summary>
    private static bool SystemUsesDark() => ReadsDark("AppsUseLightTheme");

    private static bool ReadsDark(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue(value) is int light && light == 0;
    }
}
