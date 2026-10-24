using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GestureSpike;

internal enum FileKind { Image, Video, Audio, Document, Mixed, Unknown }

/// <summary>Which output formats the wheel offers for a set of dropped files.</summary>
internal static class Formats
{
    // Only image conversion is real in the prototype; these are the formats WPF can write.
    private static readonly string[] ImageTargets = { "png", "jpg", "bmp", "gif", "tiff" };
    private static readonly string[] VideoTargets = { "mp4", "webm", "mov", "gif", "mp3" };
    private static readonly string[] AudioTargets = { "mp3", "wav", "flac", "ogg", "m4a" };
    private static readonly string[] DocumentTargets = { "pdf", "docx", "txt", "md" };

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
        ["rtf"] = FileKind.Document, ["md"] = FileKind.Document, ["txt"] = FileKind.Document, ["html"] = FileKind.Document,
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

    public static (FileKind Kind, string[] Targets) For(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return (FileKind.Unknown, Array.Empty<string>());

        var kinds = files.Select(KindOf).Distinct().ToList();
        if (kinds.Count > 1)
            return (FileKind.Mixed, Array.Empty<string>());

        var kind = kinds[0];
        string[] targets = kind switch
        {
            FileKind.Image => ImageTargets,
            FileKind.Video => VideoTargets,
            FileKind.Audio => AudioTargets,
            FileKind.Document => DocumentTargets,
            _ => Array.Empty<string>(),
        };

        // Don't offer "convert to the format it already is" when every file shares that format.
        var sourceExts = files.Select(NormalizedExtension).Distinct().ToList();
        if (sourceExts.Count == 1)
            targets = targets.Where(t => t != sourceExts[0]).ToArray();

        return (kind, targets);
    }
}
