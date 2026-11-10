using Circularity;
using Xunit;

namespace Circularity.UnitTests;

/// <summary>The gesture only opens the wheel while Windows is carrying a drag, not while an app follows the mouse.</summary>
public class GestureTests
{
    [Theory]
    [InlineData(0, App.OleDragWindowClass, true)]        // A file, link or picture being dragged.
    [InlineData(0, null, true)]                          // Nothing to go by: as before.
    [InlineData(0, "DirectUIHWND", false)]               // Explorer drawing a selection box.
    [InlineData(0, "Chrome_RenderWidgetHostHWND", false)] // Selecting text in a browser.
    [InlineData(Native.GUI_INMOVESIZE, null, false)]     // Moving a window by its title bar.
    [InlineData(Native.GUI_INMENUMODE, null, false)]     // A menu is open.
    [InlineData(1, App.OleDragWindowClass, true)]        // Other flags (the caret blinking) don't matter.
    public void ArmsOnlyForADrag(int flags, string? capture, bool arms) =>
        Assert.Equal(arms, App.ArmsFor(flags, capture));
}
