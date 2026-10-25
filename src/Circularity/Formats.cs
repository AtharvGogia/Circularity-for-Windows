using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Circularity.Engine;

namespace Circularity;

internal enum FileKind { Image, Video, Audio, Document, Mixed, Unknown }

internal enum WheelActionKind { Convert, Resize, Crop, Compress, Combine, Preset, More }

/// <summary>One button on the wheel. <paramref name="Preset"/> is set for a saved resize or compress.</summary>
internal sealed record WheelAction(WheelActionKind Kind, string Label, string Glyph, string? Target = null, Preset? Preset = null)
{
    public bool IsTool => Kind is not (WheelActionKind.Convert or WheelActionKind.More);

    /// <summary>Asks for something (a size, a crop) before it runs, rather than running straight away.</summary>
    public bool OpensDialog => Kind is WheelActionKind.Resize or WheelActionKind.Crop or WheelActionKind.Compress;

    /// <summary>Stays the same across versions and files, for remembering the user's order: "png", "resize", "preset:1a2b3c4d".</summary>
    public string Id => Kind switch
    {
        WheelActionKind.Convert => Target!,
        WheelActionKind.Preset => "preset:" + Preset!.Id,
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>Icon glyphs from Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).</summary>
internal static class Glyphs
{
    public const string Photo = "";
    public const string Video = "";
    public const string Music = "";
    public const string Gif = "";
    public const string Pdf = "";
    public const string Document = "";
    public const string Web = "";
    public const string Resize = "";
    public const string Crop = "";
    public const string Compress = "";
    public const string Combine = "";
    public const string Cancel = "";
    public const string More = "";
}

/// <summary>Which buttons the wheel offers for a set of dropped files.</summary>
internal static class Formats
{
    // Everything worth offering per source format. Buttons only appear when some installed
    // converter can actually do it for every dropped file (Office ones need Office, for example).
    // Order matters when there are more than the wheel's six buttons: the first ones get a place on
    // the ring and the later ones go behind "More".
    private static readonly string[] ImageTargets = { "png", "jpg", "webp", "pdf", "gif", "bmp", "tiff" };
    private static readonly string[] VideoTargets = { "mp4", "webm", "mov", "gif", "mp3" };
    private static readonly string[] AudioTargets = { "mp3", "wav", "flac", "ogg", "m4a" };
    private static readonly Dictionary<string, string[]> DocumentTargets = new()
    {
        ["pdf"] = new[] { "png", "jpg", "txt" },
        ["docx"] = new[] { "pdf", "txt" },
        ["doc"] = new[] { "pdf", "docx", "txt" },
        ["rtf"] = new[] { "pdf", "docx", "txt" },
        ["odt"] = new[] { "pdf", "docx", "txt" },
        ["md"] = new[] { "pdf", "html" },
        ["html"] = new[] { "pdf" },
        ["txt"] = new[] { "pdf" },
        ["xlsx"] = new[] { "pdf" }, ["xls"] = new[] { "pdf" }, ["ods"] = new[] { "pdf" }, ["csv"] = new[] { "pdf" },
        ["pptx"] = new[] { "pdf" }, ["ppt"] = new[] { "pdf" }, ["odp"] = new[] { "pdf" },
    };

    // Documents only Microsoft Office can open.
    private static readonly HashSet<string> OfficeExtensions = new()
    {
        "docx", "doc", "rtf", "odt", "xlsx", "xls", "ods", "csv", "pptx", "ppt", "odp",
    };

    private static readonly WheelAction Resize = new(WheelActionKind.Resize, "Resize", Glyphs.Resize);
    private static readonly WheelAction Crop = new(WheelActionKind.Crop, "Crop", Glyphs.Crop);
    private static readonly WheelAction Compress = new(WheelActionKind.Compress, "Compress", Glyphs.Compress);
    private static readonly WheelAction Combine = new(WheelActionKind.Combine, "Combine", Glyphs.Combine);

    private static readonly Dictionary<FileKind, WheelAction[]> ToolsByKind = new()
    {
        [FileKind.Image] = new[] { Resize, Compress, Crop },
        [FileKind.Video] = new[] { Resize, Crop },
    };

    private static readonly Dictionary<string, FileKind> KindByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = FileKind.Image, ["jpg"] = FileKind.Image, ["jpeg"] = FileKind.Image, ["jfif"] = FileKind.Image,
        ["bmp"] = FileKind.Image, ["gif"] = FileKind.Image, ["tif"] = FileKind.Image, ["tiff"] = FileKind.Image,
        ["webp"] = FileKind.Image, ["heic"] = FileKind.Image, ["heif"] = FileKind.Image, ["ico"] = FileKind.Image,

        ["mp4"] = FileKind.Video, ["mov"] = FileKind.Video, ["mkv"] = FileKind.Video, ["avi"] = FileKind.Video,
        ["webm"] = FileKind.Video, ["wmv"] = FileKind.Video, ["m4v"] = FileKind.Video,

        ["mp3"] = FileKind.Audio, ["wav"] = FileKind.Audio, ["flac"] = FileKind.Audio, ["ogg"] = FileKind.Audio,
        ["m4a"] = FileKind.Audio, ["aac"] = FileKind.Audio, ["wma"] = FileKind.Audio, ["opus"] = FileKind.Audio,

        ["pdf"] = FileKind.Document, ["docx"] = FileKind.Document, ["doc"] = FileKind.Document,
        ["odt"] = FileKind.Document, ["rtf"] = FileKind.Document, ["txt"] = FileKind.Document,
        ["md"] = FileKind.Document, ["markdown"] = FileKind.Document, ["html"] = FileKind.Document, ["htm"] = FileKind.Document,
        ["xlsx"] = FileKind.Document, ["xls"] = FileKind.Document, ["ods"] = FileKind.Document, ["csv"] = FileKind.Document,
        ["pptx"] = FileKind.Document, ["ppt"] = FileKind.Document, ["odp"] = FileKind.Document,
    };

    /// <summary>Lower-case extension without the dot, with common aliases folded together.</summary>
    public static string NormalizedExtension(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpeg" or "jfif" => "jpg",
            "tif" => "tiff",
            "markdown" => "md",
            "htm" => "html",
            _ => ext,
        };
    }

    public static FileKind KindOf(string path) =>
        KindByExtension.TryGetValue(Path.GetExtension(path).TrimStart('.'), out var kind) ? kind : FileKind.Unknown;

    /// <summary>The wheel's buttons for these files, and a short description for its centre.</summary>
    /// <param name="canHandle">Whether an installed converter can apply an operation to a file.</param>
    /// <param name="settings">Adds the user's presets and applies their button order; null for the built-in buttons.</param>
    public static (string Label, WheelAction[] Actions) ActionsFor(IReadOnlyList<string> files, Func<string, Operation, bool> canHandle,
        Settings? settings = null)
    {
        if (files.Count == 0)
            return ("Not a file", Array.Empty<WheelAction>());

        var kinds = files.Select(KindOf).Distinct().ToList();
        var exts = files.Select(NormalizedExtension).Distinct().ToList();

        // Formats every file can become, minus "the format they already all are".
        var common = files
            .Select(CandidateTargets)
            .Aggregate((a, b) => a.Intersect(b).ToArray())
            .Where(t => !(exts.Count == 1 && exts[0] == t))
            .ToList();
        var targets = common.Where(t => files.All(f => canHandle(f, new ConvertTo(t))));
        var actions = targets.Select(ConvertAction).ToList();

        // Tools every file supports (resize and crop work on a mix of images and videos).
        var tools = kinds
            .Select(k => ToolsByKind.TryGetValue(k, out var t) ? t : Array.Empty<WheelAction>())
            .Aggregate((a, b) => a.Intersect(b).ToArray());
        actions.AddRange(tools);

        if (files.Count > 1 && canHandle(files[0], new CombineIntoPdf(files)))
            actions.Add(Combine);

        if (settings != null)
        {
            // A preset appears wherever its tool does.
            actions.AddRange(PresetActions(settings, tools)
                .Where(a => files.All(f => canHandle(f, a.Preset!.Operation))));
            if (kinds.Count == 1 && settings.ButtonLayouts.TryGetValue(kinds[0].ToString(), out var layout))
                actions = Arrange(actions, layout).Where(a => !layout.Hidden.Contains(a.Id)).ToList();
        }

        string label = kinds.Count > 1
            ? $"{files.Count} files"
            : kinds[0] switch
            {
                FileKind.Unknown => "Unsupported type",
                FileKind.Document => files.Count == 1 ? exts[0].ToUpperInvariant() : $"{files.Count} documents",
                var kind => files.Count == 1 ? kind.ToString() : $"{files.Count} {kind.ToString().ToLowerInvariant()}s",
            };
        // Only blame Office when there was something to offer and Office is what's missing for it;
        // documents that simply share no format (a PDF and a spreadsheet, say) are a different story.
        if (actions.Count == 0 && kinds.Count == 1 && kinds[0] == FileKind.Document)
        {
            label = common.Count == 0 ? "No format in common"
                : files.Any(f => OfficeExtensions.Contains(NormalizedExtension(f))) ? "Needs Microsoft Office"
                : "Can't convert these";
        }
        return (label, actions.ToArray());
    }

    /// <summary>The kinds of file whose buttons can be rearranged in Settings.</summary>
    public static readonly FileKind[] CustomizableKinds = { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Document };

    /// <summary>
    /// Every button the wheel can ever show for a kind of file, in the user's order (built-in order
    /// when they haven't changed it), hidden ones included. Which ones actually appear depends on the
    /// files and on what this PC can do.
    /// </summary>
    public static List<WheelAction> AllButtonsFor(FileKind kind, Settings settings)
    {
        var targets = kind switch
        {
            FileKind.Image => ImageTargets,
            FileKind.Video => VideoTargets,
            FileKind.Audio => AudioTargets,
            FileKind.Document => DocumentTargets.Values.SelectMany(t => t).Distinct().ToArray(),
            _ => Array.Empty<string>(),
        };
        var tools = ToolsByKind.TryGetValue(kind, out var t) ? t : Array.Empty<WheelAction>();
        var all = targets.Select(ConvertAction).Concat(tools).ToList();
        if (kind is FileKind.Image or FileKind.Document)
            all.Add(Combine);
        all.AddRange(PresetActions(settings, tools));
        return settings.ButtonLayouts.TryGetValue(kind.ToString(), out var layout) ? Arrange(all, layout) : all;
    }

    /// <summary>Whether a file of this type is ever offered a conversion to <paramref name="target"/>.</summary>
    public static bool Offers(string file, string target) =>
        NormalizedExtension(file) != target && CandidateTargets(file).Contains(target);

    /// <summary>The user's order first, then anything they haven't placed yet (new presets, say) in built-in order.</summary>
    private static List<WheelAction> Arrange(List<WheelAction> actions, ButtonLayout layout) => actions
        .Select((action, index) => (action, index))
        .OrderBy(x => layout.Order.IndexOf(x.action.Id) is var at and >= 0 ? at : int.MaxValue)
        .ThenBy(x => x.index)
        .Select(x => x.action)
        .ToList();

    /// <summary>The presets whose tool (resize or compress) is among <paramref name="tools"/>.</summary>
    private static IEnumerable<WheelAction> PresetActions(Settings settings, IEnumerable<WheelAction> tools)
    {
        bool resize = tools.Contains(Resize), compress = tools.Contains(Compress);
        foreach (var preset in settings.Presets)
        {
            if (preset.Resize != null && resize)
                yield return new WheelAction(WheelActionKind.Preset, preset.Name, Glyphs.Resize, Preset: preset);
            else if (preset.Compress != null && compress)
                yield return new WheelAction(WheelActionKind.Preset, preset.Name, Glyphs.Compress, Preset: preset);
        }
    }

    private static WheelAction ConvertAction(string target) =>
        new(WheelActionKind.Convert, target.ToUpperInvariant(), GlyphFor(target), target);

    private static string[] CandidateTargets(string file) => KindOf(file) switch
    {
        FileKind.Image => ImageTargets,
        FileKind.Video => VideoTargets,
        FileKind.Audio => AudioTargets,
        FileKind.Document => DocumentTargets.TryGetValue(NormalizedExtension(file), out var targets) ? targets : Array.Empty<string>(),
        _ => Array.Empty<string>(),
    };

    private static string GlyphFor(string target) => target switch
    {
        "gif" => Glyphs.Gif,
        "mp4" or "webm" or "mov" => Glyphs.Video,
        "mp3" or "wav" or "flac" or "ogg" or "m4a" => Glyphs.Music,
        "pdf" => Glyphs.Pdf,
        "docx" or "txt" => Glyphs.Document,
        "html" => Glyphs.Web,
        _ => Glyphs.Photo,
    };
}
