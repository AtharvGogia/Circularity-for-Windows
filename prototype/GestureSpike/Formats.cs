using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GestureSpike;

internal enum FileKind { Image, Video, Audio, Document, Mixed, Unknown }

internal enum WheelActionKind { Convert, Resize, Crop, Compress }

/// <summary>One button on the wheel.</summary>
internal sealed record WheelAction(WheelActionKind Kind, string Label, string Glyph, string? Target = null)
{
    public bool IsTool => Kind != WheelActionKind.Convert;
}

/// <summary>Icon glyphs from Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).</summary>
internal static class Glyphs
{
    public const string Photo = "";
    public const string Video = "";
    public const string Music = "";
    public const string Gif = "";
    public const string Resize = "";
    public const string Crop = "";
    public const string Compress = "";
    public const string Cancel = "";
}

/// <summary>Which buttons the wheel offers for a set of dropped files.</summary>
internal static class Formats
{
    private static readonly string[] ImageTargets = { "png", "jpg", "bmp", "gif", "tiff" };
    private static readonly string[] VideoTargets = { "mp4", "webm", "mov", "gif", "mp3" };
    private static readonly string[] AudioTargets = { "mp3", "wav", "flac", "ogg", "m4a" };

    private static readonly WheelAction Resize = new(WheelActionKind.Resize, "Resize", Glyphs.Resize);
    private static readonly WheelAction Crop = new(WheelActionKind.Crop, "Crop", Glyphs.Crop);
    private static readonly WheelAction Compress = new(WheelActionKind.Compress, "Compress", Glyphs.Compress);

    private static readonly WheelAction[] ImageTools = { Resize, Crop, Compress };
    private static readonly WheelAction[] VideoTools = { Resize, Crop };

    private static readonly Dictionary<string, FileKind> KindByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = FileKind.Image, ["jpg"] = FileKind.Image, ["jpeg"] = FileKind.Image, ["jfif"] = FileKind.Image,
        ["bmp"] = FileKind.Image, ["gif"] = FileKind.Image, ["tif"] = FileKind.Image, ["tiff"] = FileKind.Image,
        ["webp"] = FileKind.Image, ["heic"] = FileKind.Image, ["heif"] = FileKind.Image, ["ico"] = FileKind.Image,

        ["mp4"] = FileKind.Video, ["mov"] = FileKind.Video, ["mkv"] = FileKind.Video, ["avi"] = FileKind.Video,
        ["webm"] = FileKind.Video, ["wmv"] = FileKind.Video, ["m4v"] = FileKind.Video,

        ["mp3"] = FileKind.Audio, ["wav"] = FileKind.Audio, ["flac"] = FileKind.Audio, ["ogg"] = FileKind.Audio,
        ["m4a"] = FileKind.Audio, ["aac"] = FileKind.Audio, ["wma"] = FileKind.Audio, ["opus"] = FileKind.Audio,

        ["docx"] = FileKind.Document, ["doc"] = FileKind.Document, ["odt"] = FileKind.Document,
        ["rtf"] = FileKind.Document, ["md"] = FileKind.Document, ["txt"] = FileKind.Document,
        ["html"] = FileKind.Document, ["pdf"] = FileKind.Document,
    };

    /// <summary>Lower-case extension without the dot, with common aliases folded together.</summary>
    public static string NormalizedExtension(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpeg" or "jfif" => "jpg",
            "tif" => "tiff",
            _ => ext,
        };
    }

    public static FileKind KindOf(string path) =>
        KindByExtension.TryGetValue(Path.GetExtension(path).TrimStart('.'), out var kind) ? kind : FileKind.Unknown;

    /// <summary>The wheel's buttons for these files, and a short description for its centre.</summary>
    public static (string Label, WheelAction[] Actions) ActionsFor(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return ("Not a file", Array.Empty<WheelAction>());

        var kinds = files.Select(KindOf).Distinct().ToList();
        if (kinds.Count > 1)
        {
            // Resize and crop work on any mix of images and videos; compress is images only.
            return kinds.All(k => k is FileKind.Image or FileKind.Video)
                ? ($"{files.Count} files", VideoTools)
                : ("Mixed file types", Array.Empty<WheelAction>());
        }

        var kind = kinds[0];
        string[] targets = kind switch
        {
            FileKind.Image => ImageTargets,
            FileKind.Video => VideoTargets,
            FileKind.Audio => AudioTargets,
            _ => Array.Empty<string>(),
        };

        // Don't offer "convert to the format it already is" when every file shares that format.
        var sourceExts = files.Select(NormalizedExtension).Distinct().ToList();
        if (sourceExts.Count == 1)
            targets = targets.Where(t => t != sourceExts[0]).ToArray();

        var actions = targets.Select(t => new WheelAction(WheelActionKind.Convert, t.ToUpperInvariant(), GlyphFor(t), t));
        if (kind == FileKind.Image)
            actions = actions.Concat(ImageTools);
        else if (kind == FileKind.Video)
            actions = actions.Concat(VideoTools);

        string label = kind switch
        {
            FileKind.Document => "Documents coming soon",
            FileKind.Unknown => "Unsupported type",
            _ => files.Count == 1 ? kind.ToString() : $"{files.Count} {kind.ToString().ToLowerInvariant()}s",
        };
        return (label, actions.ToArray());
    }

    private static string GlyphFor(string target) => target switch
    {
        "gif" => Glyphs.Gif,
        "mp4" or "webm" or "mov" => Glyphs.Video,
        "mp3" or "wav" or "flac" or "ogg" or "m4a" => Glyphs.Music,
        _ => Glyphs.Photo,
    };
}
