using Crowbar.Engine.Platform;
using Silk.NET.SDL;
using Xunit;

namespace Crowbar.Engine.Platform.Tests;

/// <summary>
/// The CSS <c>cursor</c> keywords are mapped to SDL system cursors by
/// <see cref="SdlInputSource.MapCursor"/>. Unknown keywords keep the current
/// cursor (null); SDL has no zoom cursors, so zoom-in/out fall back to the
/// hand.
/// </summary>
public class CursorMappingTests
{
    [Theory]
    [InlineData("auto", SystemCursor.SystemCursorArrow)]
    [InlineData("default", SystemCursor.SystemCursorArrow)]
    [InlineData("help", SystemCursor.SystemCursorArrow)]
    [InlineData("pointer", SystemCursor.SystemCursorHand)]
    [InlineData("grab", SystemCursor.SystemCursorHand)]
    [InlineData("grabbing", SystemCursor.SystemCursorHand)]
    [InlineData("zoom-in", SystemCursor.SystemCursorHand)]
    [InlineData("zoom-out", SystemCursor.SystemCursorHand)]
    [InlineData("text", SystemCursor.SystemCursorIbeam)]
    [InlineData("crosshair", SystemCursor.SystemCursorCrosshair)]
    [InlineData("wait", SystemCursor.SystemCursorWait)]
    [InlineData("progress", SystemCursor.SystemCursorWaitarrow)]
    [InlineData("move", SystemCursor.SystemCursorSizeall)]
    [InlineData("all-scroll", SystemCursor.SystemCursorSizeall)]
    [InlineData("ew-resize", SystemCursor.SystemCursorSizewe)]
    [InlineData("col-resize", SystemCursor.SystemCursorSizewe)]
    [InlineData("ns-resize", SystemCursor.SystemCursorSizens)]
    [InlineData("row-resize", SystemCursor.SystemCursorSizens)]
    [InlineData("nesw-resize", SystemCursor.SystemCursorSizenesw)]
    [InlineData("nwse-resize", SystemCursor.SystemCursorSizenwse)]
    [InlineData("not-allowed", SystemCursor.SystemCursorNo)]
    [InlineData("bogus", null)]
    [InlineData("", null)]
    public void MapCursorMapsKeywords(string css, SystemCursor? expected) =>
        Assert.Equal(expected, SdlInputSource.MapCursor(css));
}
