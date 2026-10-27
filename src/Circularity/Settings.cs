using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Circularity.Engine;

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

    /// <summary>Copy the converted file path(s) to the clipboard when conversions finish.</summary>
    public bool CopyToClipboardOnFinish { get; set; } = false;

    /// <summary>Automatically check for updates in the background.</summary>
    public bool AutoCheckForUpdates { get; set; } = false;

    /// <summary>How often to check for updates, in hours: 0 (only at start) or 1 to 720 (30 days).</summary>
    public int AutoCheckIntervalHours { get; set; } = 24;

    /// <summary>The newest release the background check has already mentioned, so it's mentioned only once.</summary>
    public string? LastNotifiedUpdate { get; set; }

    public static int ClampUpdateInterval(int hours) => hours <= 0 ? 0 : Math.Min(hours, 720);

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Saved resize and compress settings, offered on the wheel as one-click buttons.</summary>
    public List<Preset> Presets { get; set; } = new();

    /// <summary>
    /// The user's button order, and buttons left off the wheel, per kind of file ("Image", "Video"...).
    /// Kinds that aren't listed use the built-in order.
    /// </summary>
    public Dictionary<string, ButtonLayout> ButtonLayouts { get; set; } = new();

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

    /// <summary>A copy that shares nothing with this one, lists included.</summary>
    public Settings Clone()
    {
        var copy = (Settings)MemberwiseClone();
        copy.Presets = new List<Preset>(Presets);
        copy.ButtonLayouts = ButtonLayouts.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        return copy;
    }

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
        Presets = (Presets ?? new()).Where(p => p is { Id.Length: > 0, Name.Length: > 0 } && (p.Resize != null) != (p.Compress != null)).ToList();
        ButtonLayouts ??= new();
        return this;
    }
}

/// <summary>A saved resize or compress (exactly one of the two), shown on the wheel under its name.</summary>
/// <param name="Kind">For a compress, the kind of file it was saved from ("Image", "Video", "Document"), since
/// "80%" or "under 5 MB" mean different things for each. Older presets have none, and were for images.</param>
internal sealed record Preset(string Id, string Name, ResizeSpec? Resize = null, CompressSpec? Compress = null, string? Kind = null)
{
    public const int MaxNameLength = 14;

    public FileKind CompressesKind => Enum.TryParse<FileKind>(Kind, out var kind) ? kind : FileKind.Image;

    public Operation Operation => Resize != null ? new ResizeTo(Resize) : new CompressTo(Compress!);

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>For one kind of file: button IDs (see <see cref="WheelAction.Id"/>) in the user's order, and the hidden ones.</summary>
internal sealed class ButtonLayout
{
    public List<string> Order { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    public ButtonLayout Clone() => new() { Order = new List<string>(Order), Hidden = new List<string>(Hidden) };
}
