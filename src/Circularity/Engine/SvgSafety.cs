using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Circularity.Engine;

/// <summary>
/// Makes an SVG safe to draw before Svg.Skia sees it. An SVG can link to pictures, fonts and styles
/// anywhere, and Svg.Skia fetches them, so just converting someone's SVG could tell their server
/// you opened it (or read other files on this PC into the picture). Everything here is self-contained
/// afterwards: links to other files or web addresses are dropped, while pictures embedded in the SVG
/// itself ("data:" links) and links within it ("#name") keep working.
/// </summary>
internal static partial class SvgSafety
{
    /// <summary>The SVG, cleaned, ready to hand to Svg.Skia.</summary>
    public static MemoryStream Clean(string path)
    {
        var settings = new XmlReaderSettings
        {
            // Illustrator and Inkscape files declare entities in a DOCTYPE, so those are allowed,
            // but nothing outside the file is ever fetched, and runaway entity expansion is capped.
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 1_000_000,
        };
        XDocument document;
        using (var reader = XmlReader.Create(path, settings))
            document = XDocument.Load(reader);
        document.DocumentType?.Remove();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.Name.LocalName == "href" && !IsSelfContained(attribute.Value))
                    attribute.Remove();
                else if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
                    attribute.Value = CleanCss(attribute.Value);
            }
            // <style> blocks: no @import, and no url() pointing outside the file.
            if (element.Name.LocalName == "style")
                element.Value = ImportRegex().Replace(CleanCss(element.Value), "");
        }

        var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        return output;
    }

    /// <summary>A link to something inside the SVG itself: a name in it, or embedded data.</summary>
    private static bool IsSelfContained(string link)
    {
        string trimmed = link.Trim();
        return trimmed.StartsWith('#') || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces every url(...) that isn't self-contained with "none".</summary>
    private static string CleanCss(string css) =>
        UrlRegex().Replace(css, match => IsSelfContained(match.Groups[1].Value.Trim('\'', '"', ' ')) ? match.Value : "none");

    [GeneratedRegex(@"url\(\s*([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex ImportRegex();
}
