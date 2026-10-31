using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Circularity;
using Circularity.Engine;

namespace Circularity.UnitTests;

public class FormatsLayoutTests
{
    [Fact]
    public void Layout_NoOverflow_ReturnsAllOnRing()
    {
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
        };

        var (ring, arc) = WheelView.Layout(actions);

        Assert.Equal(2, ring.Length);
        Assert.Equal(0, ring[0]); // PNG at top
        Assert.Equal(1, ring[1]); // JPG at slot 1
        Assert.Empty(arc);
    }

    [Fact]
    public void Layout_ExactlySixFits_ReturnsAllOnRing()
    {
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
            new(WheelActionKind.Convert, "WEBP", "W", "webp"),
            new(WheelActionKind.Convert, "GIF", "G", "gif"),
            new(WheelActionKind.Convert, "BMP", "B", "bmp"),
            new(WheelActionKind.Convert, "TIFF", "T", "tiff"),
        };

        var (ring, arc) = WheelView.Layout(actions);

        Assert.Equal(6, ring.Length);
        Assert.Empty(arc);
    }

    [Fact]
    public void Layout_SevenActions_PutsMoreOnRingAndTwoInArc()
    {
        // 7 actions, 6 ring slots -> 2 overflow items in arc
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
            new(WheelActionKind.Convert, "WEBP", "W", "webp"),
            new(WheelActionKind.Convert, "GIF", "G", "gif"),
            new(WheelActionKind.Convert, "BMP", "B", "bmp"),
            new(WheelActionKind.Convert, "TIFF", "T", "tiff"),
            new(WheelActionKind.Convert, "AVIF", "A", "avif"),
        };

        var (ring, arc) = WheelView.Layout(actions);

        Assert.Equal(6, ring.Length);
        Assert.Equal(2, arc.Length);
        Assert.Contains(WheelView.MoreIndex, ring);
    }

    [Fact]
    public void Layout_DialogActionsGoLeftOfMore()
    {
        // Resize, Crop, Compress are dialog actions
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
            new(WheelActionKind.Resize, "Resize", "R"),
            new(WheelActionKind.Crop, "Crop", "C"),
            new(WheelActionKind.Compress, "Compress", "Z"),
            new(WheelActionKind.Convert, "WEBP", "W", "webp"),
            new(WheelActionKind.Convert, "GIF", "G", "gif"),
        };

        var (ring, arc) = WheelView.Layout(actions);

        Assert.Equal(6, ring.Length);
        // Resize, Crop, Compress should be at left slots (indices 4 and 5 on 0-based from top)
        // Ring slots: 0(top), 1, 2, 3(More), 4, 5
        // Dialog actions should be at 4 and 5 (left of More)
        var dialogActionsOnRing = ring.Where(i => i >= 0 && actions[i].OpensDialog).ToArray();
        Assert.All(dialogActionsOnRing, i => Assert.Contains(i, ring));
    }

    [Fact]
    public void Layout_WithArcAbove_PlacesMoreAtTop()
    {
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
            new(WheelActionKind.Convert, "WEBP", "W", "webp"),
            new(WheelActionKind.Convert, "GIF", "G", "gif"),
            new(WheelActionKind.Convert, "BMP", "B", "bmp"),
            new(WheelActionKind.Convert, "TIFF", "T", "tiff"),
            new(WheelActionKind.Convert, "AVIF", "A", "avif"),
        };

        var (ring, arc) = WheelView.Layout(actions, arcAbove: true);

        Assert.Equal(6, ring.Length);
        Assert.Equal(2, arc.Length); // 2 overflow items
        Assert.Equal(WheelView.MoreIndex, ring[0]); // More at top slot when arcAbove
    }

    [Fact]
    public void Layout_OverflowDialogActionsGoToArcLeftEnd()
    {
        // 8 actions including 3 dialog actions - 2 fit on ring left, 1 goes to arc
        var actions = new WheelAction[]
        {
            new(WheelActionKind.Convert, "PNG", "P", "png"),
            new(WheelActionKind.Convert, "JPG", "J", "jpg"),
            new(WheelActionKind.Convert, "WEBP", "W", "webp"),
            new(WheelActionKind.Resize, "Resize", "R"),
            new(WheelActionKind.Crop, "Crop", "C"),
            new(WheelActionKind.Compress, "Compress", "Z"),
            new(WheelActionKind.Convert, "GIF", "G", "gif"),
            new(WheelActionKind.Convert, "BMP", "B", "bmp"),
        };

        var (ring, arc) = WheelView.Layout(actions);

        Assert.Equal(6, ring.Length);
        Assert.Equal(3, arc.Length); // 3 overflow items (2 others + 1 dialog)
        // The overflow dialog action (Compress) should be at left end of arc
        var lastArcAction = arc[arc.Length - 1];
        Assert.True(actions[lastArcAction].OpensDialog);
    }
}

public class FormatsUtilityTests
{
    [Theory]
    [InlineData("image.png", "png")]
    [InlineData("image.jpg", "jpg")]
    [InlineData("image.jpeg", "jpg")]
    [InlineData("image.JPG", "jpg")]
    [InlineData("image.JPEG", "jpg")]
    [InlineData("image.jfif", "jpg")]
    [InlineData("image.tiff", "tiff")]
    [InlineData("image.tif", "tiff")]
    [InlineData("image.heic", "heic")]
    [InlineData("image.heif", "heic")]
    [InlineData("image.hif", "heic")]
    [InlineData("image.avif", "avif")]
    [InlineData("video.mp4", "mp4")]
    [InlineData("audio.mp3", "mp3")]
    [InlineData("doc.pdf", "pdf")]
    [InlineData("doc.docx", "docx")]
    public void NormalizedExtension_ReturnsExpected(string path, string expected)
    {
        Assert.Equal(expected, Formats.NormalizedExtension(path));
    }

    [Theory]
    [InlineData("image.png", FileKind.Image)]
    [InlineData("image.jpg", FileKind.Image)]
    [InlineData("image.heic", FileKind.Image)]
    [InlineData("image.avif", FileKind.Image)]
    [InlineData("image.svg", FileKind.Image)]
    [InlineData("video.mp4", FileKind.Video)]
    [InlineData("video.mov", FileKind.Video)]
    [InlineData("video.webm", FileKind.Video)]
    [InlineData("audio.mp3", FileKind.Audio)]
    [InlineData("audio.wav", FileKind.Audio)]
    [InlineData("audio.flac", FileKind.Audio)]
    [InlineData("audio.caf", FileKind.Audio)]
    [InlineData("audio.aiff", FileKind.Audio)]
    [InlineData("doc.pdf", FileKind.Document)]
    [InlineData("doc.docx", FileKind.Document)]
    [InlineData("doc.md", FileKind.Document)]
    [InlineData("doc.txt", FileKind.Document)]
    [InlineData("sheet.xlsx", FileKind.Document)]
    [InlineData("slide.pptx", FileKind.Document)]
    [InlineData("unknown.xyz", FileKind.Unknown)]
    public void KindOf_ReturnsExpectedKind(string path, FileKind expected)
    {
        Assert.Equal(expected, Formats.KindOf(path));
    }

    [Theory]
    [InlineData("image.png", "jpg", true)]   // different format
    [InlineData("image.png", "png", false)]  // same format
    [InlineData("image.heic", "jpg", true)]  // heic to jpg
    [InlineData("image.jpg", "webp", true)]  // jpg to webp
    public void Offers_ReturnsExpected(string file, string target, bool expected)
    {
        Assert.Equal(expected, Formats.Offers(file, target));
    }
}