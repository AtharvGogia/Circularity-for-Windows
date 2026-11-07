using System.Linq;
using Xunit;
using Circularity;
using Circularity.Engine;

namespace Circularity.UnitTests;

public class ButtonOrderTests
{
    private static string[] ImageButtons(Settings settings) =>
        Formats.ActionsFor(new[] { "photo.jpg" }, (_, _) => true, settings).Actions.Select(a => a.Id).ToArray();

    [Fact]
    public void BmpAndTiff_AreHiddenByDefault()
    {
        var ids = ImageButtons(new Settings());
        Assert.DoesNotContain("bmp", ids);
        Assert.DoesNotContain("tiff", ids);
        Assert.Contains("png", ids);
    }

    [Fact]
    public void BmpAndTiff_ShowOnceTheButtonsAreChanged()
    {
        var settings = new Settings { ButtonLayouts = { ["Image"] = new ButtonLayout() } };
        var ids = ImageButtons(settings);
        Assert.Contains("bmp", ids);
        Assert.Contains("tiff", ids);
    }

    [Fact]
    public void HiddenByDefault_StillListedInSettings()
    {
        var all = Formats.AllButtonsFor(FileKind.Image, new Settings()).Select(a => a.Id).ToArray();
        Assert.Contains("bmp", all);
        Assert.Equal(new[] { "bmp", "tiff" }, Formats.LayoutFor(new Settings(), FileKind.Image).Hidden);
        Assert.Empty(Formats.LayoutFor(new Settings(), FileKind.Video).Hidden);
    }

    [Fact]
    public void AdaptToUse_PutsTheMostUsedFirst()
    {
        var settings = new Settings();
        for (int i = 0; i < 3; i++)
            settings.CountUse(FileKind.Image, "heic");
        settings.CountUse(FileKind.Image, "gif");

        var ids = ImageButtons(settings);
        Assert.Equal("heic", ids[0]);
        Assert.Equal("gif", ids[1]);
        // Never used: the built-in order, after them.
        Assert.Equal(new[] { "png", "webp", "pdf" }, ids.Skip(2).Take(3).ToArray());
    }

    [Fact]
    public void AdaptToUse_Off_KeepsTheBuiltInOrder()
    {
        var settings = new Settings { AdaptToUse = false };
        settings.CountUse(FileKind.Image, "heic");
        Assert.Equal("png", ImageButtons(settings)[0]);
    }

    [Fact]
    public void YourOwnOrder_WinsOverUse()
    {
        var settings = new Settings { ButtonLayouts = { ["Image"] = new ButtonLayout { Order = { "webp", "png" } } } };
        settings.CountUse(FileKind.Image, "heic");
        var ids = ImageButtons(settings);
        Assert.Equal(new[] { "webp", "png" }, ids.Take(2).ToArray());
    }

    [Fact]
    public void UseCounts_AreKeptPerKind_AndCopied()
    {
        var settings = new Settings();
        settings.CountUse(FileKind.Image, "png");
        settings.CountUse(FileKind.Image, "png");
        settings.CountUse(FileKind.Video, "mp4");
        Assert.Equal(2, settings.UseCounts["Image"]["png"]);
        Assert.Equal(1, settings.UseCounts["Video"]["mp4"]);

        var copy = settings.Clone();
        copy.CountUse(FileKind.Image, "png");
        Assert.Equal(2, settings.UseCounts["Image"]["png"]);
    }

    [Fact]
    public void OlderSettings_AdaptByDefault_AndRoundTrip()
    {
        var old = Settings.FromJson("""{"Version": 2}""");
        Assert.True(old.AdaptToUse);
        Assert.Empty(old.UseCounts);

        var settings = new Settings { AdaptToUse = false };
        settings.CountUse(FileKind.Image, "webp");
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = Settings.FromJson(json);
        Assert.False(back.AdaptToUse);
        Assert.Equal(1, back.UseCounts["Image"]["webp"]);
    }
}
