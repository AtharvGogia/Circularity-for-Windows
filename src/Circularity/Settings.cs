using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Circularity;

/// <summary>User preferences, stored as JSON in %APPDATA%\Circularity\settings.json.</summary>
internal sealed class Settings
{
    public const int MinWheelSize = 200;
    public const int MaxWheelSize = 360;
    public const string DefaultAccent = "#FF8A1E";

    /// <summary>Wheel diameter in device-independent pixels.</summary>
    public int WheelSize { get; set; } = 260;

    /// <summary>Accent colour as #RRGGBB, used for the highlighted button and tool buttons.</summary>
    public string AccentColor { get; set; } = DefaultAccent;

    /// <summary>Opacity of the wheel's background, 0.4 to 1. The buttons stay readable at any value.</summary>
    public double Opacity { get; set; } = 0.82;

    /// <summary>Light or dark wheel and windows, or follow Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool RightClickTrigger { get; set; } = true;
    public bool ShiftTrigger { get; set; } = true;

    /// <summary>Ctrl+Alt+C opens the wheel for the files selected in Explorer or on the desktop.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Show a notification when conversions finish. Failures are always shown.</summary>
    public bool NotifyOnFinish { get; set; } = true;

    /// <summary>Show "Circularity is running" when it starts.</summary>
    public bool NotifyOnStart { get; set; } = true;

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool WelcomeShown { get; set; }

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Circularity", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Color Accent
    {
        get
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(AccentColor);
            }
            catch (FormatException)
            {
                return (Color)ColorConverter.ConvertFromString(DefaultAccent);
            }
        }
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))?.Normalized() ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't read settings, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't save settings: {ex.Message}");
        }
    }

    /// <summary>Clamps values from a hand-edited or older settings file into range.</summary>
    private Settings Normalized()
    {
        WheelSize = Math.Clamp(WheelSize, MinWheelSize, MaxWheelSize);
        Opacity = Math.Clamp(Opacity, 0.4, 1);
        AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? DefaultAccent : AccentColor;
        return this;
    }
}
