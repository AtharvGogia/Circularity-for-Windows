using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using Circularity.Engine;

namespace Circularity;

/// <summary>User preferences, stored as JSON in %APPDATA%\Circularity\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>Current settings schema version. Increment when adding fields that need migration.</summary>
    public const int CurrentVersion = 2;

    public const int MinWheelSize = 200;
    public const int MaxWheelSize = 360;
    /// <summary>Circularity's own colour: violet since 0.5.1, orange before.</summary>
    public const string DefaultAccent = "#8B5CF6";

    /// <summary>The default up to 0.5.0, which settings from then still hold when it was never changed.</summary>
    private const string OldDefaultAccent = "#FF8A1E";

    /// <summary>Settings schema version. Missing = 1 (pre-versioning).</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Wheel diameter in device-independent pixels.</summary>
    public int WheelSize { get; set; } = 260;

    /// <summary>Accent colour as #RRGGBB, used for the highlighted button and tool buttons.</summary>
    public string AccentColor { get; set; } = DefaultAccent;

    /// <summary>Set once the user picks a colour, so a deliberate choice is never replaced by a new default.</summary>
    public bool AccentChosen { get; set; }

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

    /// <summary>Where converted files are saved: a folder, or null for next to each original.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>Copy the converted file path(s) to the clipboard when conversions finish.</summary>
    public bool CopyToClipboardOnFinish { get; set; } = false;

    /// <summary>
    /// Automatically check for updates in the background. On for new installs since 0.5.5; files from before
    /// keep what they say (false then, as that was the default).
    /// </summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>How often to check for updates, in hours: 0 (only at start) or 1 to 720 (30 days).</summary>
    public int AutoCheckIntervalHours { get; set; } = 24;

    /// <summary>The newest release the background check has already mentioned, so it's mentioned only once.</summary>
    public string? LastNotifiedUpdate { get; set; }

    /// <summary>Send the anonymous daily count (see <see cref="UsagePing"/>). Off unless the user turns it on.</summary>
    public bool ShareUsageCount { get; set; } = false;

    /// <summary>The random ID sent with the count. Made when the count is on, and dropped when it's turned off.</summary>
    public string? UsageId { get; set; }

    /// <summary>The UTC date (yyyy-MM-dd) the count was last sent, so it's sent once a day.</summary>
    public string? LastUsagePing { get; set; }

    public static int ClampUpdateInterval(int hours) => hours <= 0 ? 0 : Math.Min(hours, 720);

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Saved resize and compress settings, offered on the wheel as one-click buttons.</summary>
    public List<Preset> Presets { get; set; } = new();

    /// <summary>
    /// "Adapt to how you use it": unless the user has set their own order for a kind of file, the buttons
    /// they use most move to the front, onto the ring.
    /// </summary>
    public bool AdaptToUse { get; set; } = true;

    /// <summary>How often each button has been used, per kind of file ("Image" → "png" → 12). Counted while <see cref="AdaptToUse"/> is on.</summary>
    public Dictionary<string, Dictionary<string, int>> UseCounts { get; set; } = new();

    /// <summary>Counts one use of a button for a kind of file.</summary>
    public void CountUse(FileKind kind, string buttonId)
    {
        if (!UseCounts.TryGetValue(kind.ToString(), out var counts))
            UseCounts[kind.ToString()] = counts = new Dictionary<string, int>();
        counts[buttonId] = counts.TryGetValue(buttonId, out int n) ? n + 1 : 1;
    }

    /// <summary>Folders where every new file is converted (or has a preset applied) as soon as it arrives.</summary>
    public List<WatchedFolder> WatchedFolders { get; set; } = new();

    /// <summary>
    /// The user's button order, and buttons left off the wheel, per kind of file ("Image", "Video"...).
    /// Kinds that aren't listed use the built-in order.
    /// </summary>
    public Dictionary<string, ButtonLayout> ButtonLayouts { get; set; } = new();

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Circularity", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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
        copy.WatchedFolders = new List<WatchedFolder>(WatchedFolders);
        copy.UseCounts = UseCounts.ToDictionary(pair => pair.Key, pair => new Dictionary<string, int>(pair.Value));
        copy.ButtonLayouts = ButtonLayouts.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        return copy;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return FromJson(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't read settings, using defaults: {ex.Message}");
        }
        return new Settings().Normalized();
    }

    /// <summary>Settings as saved, brought up to date (also used by the tests).</summary>
    internal static Settings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
        if (settings == null)
            return new Settings().Normalized();
        // Files from before versioning have no "Version" at all: they're version 1, not the current one
        // the property starts out as.
        if (JsonNode.Parse(json)?["Version"] is null)
            settings.Version = 1;
        return settings.Migrate();
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

    /// <summary>Migrates settings from older versions to current.</summary>
    internal Settings Migrate()
    {
        // Version 1 -> 2: Added Version field, CopyToClipboardOnFinish default false (already default)
        if (Version < 2)
        {
            Version = 2;
            // CopyToClipboardOnFinish defaults to false, which is correct for v1->2
        }
        return Normalized();
    }

    /// <summary>Turns the usage count on or off. Turning it back on makes a new ID, unlinked to the old one.</summary>
    public void SetShareUsageCount(bool share)
    {
        ShareUsageCount = share;
        NormalizeUsage();
    }

    private void NormalizeUsage()
    {
        if (!ShareUsageCount)
        {
            UsageId = null;
            LastUsagePing = null;
        }
        else if (string.IsNullOrEmpty(UsageId))
        {
            UsageId = Guid.NewGuid().ToString();
        }
    }

    /// <summary>Clamps values from a hand-edited or older settings file into range.</summary>
    private Settings Normalized()
    {
        WheelSize = Math.Clamp(WheelSize, MinWheelSize, MaxWheelSize);
        Opacity = Math.Clamp(Opacity, 0.4, 1);
        AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? DefaultAccent : AccentColor;
        // Still on the old default, and never picked: it was never really chosen, so it moves to the new one.
        if (!AccentChosen && string.Equals(AccentColor, OldDefaultAccent, StringComparison.OrdinalIgnoreCase))
            AccentColor = DefaultAccent;
        Presets = (Presets ?? new()).Where(p => p is { Id.Length: > 0, Name.Length: > 0 } && (p.Resize != null) != (p.Compress != null)).ToList();
        ButtonLayouts ??= new();
        UseCounts ??= new();
        WatchedFolders = (WatchedFolders ?? new()).Where(w => w is { Id.Length: > 0, Path.Length: > 0, Action.Length: > 0 }).ToList();
        NormalizeUsage();
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

/// <summary>
/// A folder Circularity watches: each new file in it gets <paramref name="Action"/>, which is a target
/// to convert to ("jpg") or a saved preset ("preset:1a2b3c4d"), like <see cref="WheelAction.Id"/>.
/// </summary>
internal sealed record WatchedFolder(string Id, string Path, string Action, bool IncludeSubfolders = false, bool Enabled = true)
{
    public const string PresetPrefix = "preset:";

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>What to do to each new file, or null when it's a preset that has since been deleted.</summary>
    public Operation? OperationFor(Settings settings)
    {
        if (!Action.StartsWith(PresetPrefix, StringComparison.Ordinal))
            return new ConvertTo(Action);
        string id = Action[PresetPrefix.Length..];
        return settings.Presets.FirstOrDefault(p => p.Id == id)?.Operation;
    }
}

/// <summary>For one kind of file: button IDs (see <see cref="WheelAction.Id"/>) in the user's order, and the hidden ones.</summary>
internal sealed class ButtonLayout
{
    public List<string> Order { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    public ButtonLayout Clone() => new() { Order = new List<string>(Order), Hidden = new List<string>(Hidden) };
}
