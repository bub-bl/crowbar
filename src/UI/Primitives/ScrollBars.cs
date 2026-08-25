namespace Crowbar.UI;

/// <summary>
/// Scrollbar geometry for scrollable panels (overflow: scroll / auto). Tracks and
/// thumbs are computed from the panel's resolved layout so the renderer, hit
/// testing and drag interaction all agree on the same rectangles. All rects are
/// in global coordinates, matching <see cref="Panel.Layout"/>.
/// </summary>
public static class ScrollBars
{
    public const float Thickness = 10f;
    private const float MinThumbSize = 16f;

    /// <summary>True when a vertical scrollbar must be drawn: overflow:scroll always shows one, overflow:auto only when the content overflows.</summary>
    public static bool ShouldShowVertical(Panel panel) =>
        panel.IsScrollContainer && (panel.Overflow.Equals("scroll", StringComparison.OrdinalIgnoreCase) || panel.MaxScrollY > 0);

    /// <summary>True when a horizontal scrollbar must be drawn (see <see cref="ShouldShowVertical"/>).</summary>
    public static bool ShouldShowHorizontal(Panel panel) =>
        panel.IsScrollContainer && (panel.Overflow.Equals("scroll", StringComparison.OrdinalIgnoreCase) || panel.MaxScrollX > 0);

    /// <summary>The vertical track: full client height at the right edge, minus the horizontal scrollbar when it is visible.</summary>
    public static UiRect VerticalTrack(Panel panel)
    {
        var border = panel.LayoutBorder;
        var thickness = panel.ScrollbarThickness;
        var left = panel.Layout.Right - border.Right - thickness;
        var top = panel.Layout.Y + border.Top;
        var height = panel.Layout.Height - border.Top - border.Bottom;
        if (ShouldShowHorizontal(panel)) height -= thickness;
        return new UiRect(left, top, thickness, Math.Max(0, height));
    }

    /// <summary>The horizontal track: full client width at the bottom edge, minus the vertical scrollbar when it is visible.</summary>
    public static UiRect HorizontalTrack(Panel panel)
    {
        var border = panel.LayoutBorder;
        var thickness = panel.ScrollbarThickness;
        var left = panel.Layout.X + border.Left;
        var top = panel.Layout.Bottom - border.Bottom - thickness;
        var width = panel.Layout.Width - border.Left - border.Right;
        if (ShouldShowVertical(panel)) width -= thickness;
        return new UiRect(left, top, Math.Max(0, width), thickness);
    }

    /// <summary>Thumb rectangle for the vertical scrollbar, sized to the visible fraction of the content.</summary>
    public static UiRect VerticalThumb(Panel panel)
    {
        var track = VerticalTrack(panel);
        var client = panel.ClientHeight;
        var content = client + panel.MaxScrollY;
        var height = content > 0 ? Math.Max(MinThumbSize, track.Height * client / content) : track.Height;
        height = Math.Min(height, track.Height);
        var top = track.Y;
        if (panel.MaxScrollY > 0 && track.Height > height)
            top += panel.ScrollY / panel.MaxScrollY * (track.Height - height);
        return new UiRect(track.X, top, track.Width, height);
    }

    /// <summary>Thumb rectangle for the horizontal scrollbar (see <see cref="VerticalThumb"/>).</summary>
    public static UiRect HorizontalThumb(Panel panel)
    {
        var track = HorizontalTrack(panel);
        var client = panel.ClientWidth;
        var content = client + panel.MaxScrollX;
        var width = content > 0 ? Math.Max(MinThumbSize, track.Width * client / content) : track.Width;
        width = Math.Min(width, track.Width);
        var left = track.X;
        if (panel.MaxScrollX > 0 && track.Width > width)
            left += panel.ScrollX / panel.MaxScrollX * (track.Width - width);
        return new UiRect(left, track.Y, width, track.Height);
    }

    /// <summary>True when the point falls on a visible scrollbar of the panel.</summary>
    public static bool HitTest(Panel panel, float x, float y)
    {
        if (!panel.IsScrollContainer) return false;
        if (ShouldShowVertical(panel) && Contains(VerticalTrack(panel), x, y)) return true;
        if (ShouldShowHorizontal(panel) && Contains(HorizontalTrack(panel), x, y)) return true;
        return false;
    }

    /// <summary>True when the point falls on the panel's vertical scrollbar track.</summary>
    public static bool HitTestVertical(Panel panel, float x, float y) =>
        ShouldShowVertical(panel) && Contains(VerticalTrack(panel), x, y);

    /// <summary>True when the point falls on the panel's horizontal scrollbar track.</summary>
    public static bool HitTestHorizontal(Panel panel, float x, float y) =>
        ShouldShowHorizontal(panel) && Contains(HorizontalTrack(panel), x, y);

    /// <summary>True when the point falls on the panel's vertical scrollbar thumb.</summary>
    public static bool ContainsThumbVertical(Panel panel, float x, float y) =>
        ShouldShowVertical(panel) && Contains(VerticalThumb(panel), x, y);

    /// <summary>True when the point falls on the panel's horizontal scrollbar thumb.</summary>
    public static bool ContainsThumbHorizontal(Panel panel, float x, float y) =>
        ShouldShowHorizontal(panel) && Contains(HorizontalThumb(panel), x, y);

    /// <summary>
    /// The distance along the axis between the pointer and the thumb's leading edge,
    /// captured at press time so a thumb grab does not jump to center under the
    /// cursor. Positive when the pointer is ahead of (below/right of) the thumb edge.
    /// </summary>
    public static float ThumbGrabOffset(Panel panel, float point, bool vertical)
    {
        var thumb = vertical ? VerticalThumb(panel) : HorizontalThumb(panel);
        var edge = vertical ? thumb.Y : thumb.X;
        return point - edge;
    }

    /// <summary>
    /// Maps a pointer position along the scroll axis to a scroll offset, keeping the
    /// thumb centered under the pointer (grab offset of 0) or preserving the caller's
    /// grab offset. Clamped to the scrollable range.
    /// </summary>
    public static float OffsetFromPoint(Panel panel, float point, bool vertical, float grabOffset = 0f)
    {
        var track = vertical ? VerticalTrack(panel) : HorizontalTrack(panel);
        var thumb = vertical ? VerticalThumb(panel) : HorizontalThumb(panel);
        var max = vertical ? panel.MaxScrollY : panel.MaxScrollX;
        var range = vertical ? track.Height - thumb.Height : track.Width - thumb.Width;
        if (max <= 0 || range <= 0) return vertical ? panel.ScrollY : panel.ScrollX;
        var origin = vertical ? track.Y : track.X;
        var size = vertical ? thumb.Height : thumb.Width;
        // grabOffset 0 centers the thumb under the pointer (track click); a
        // captured grab offset anchors the thumb leading edge to the pointer.
        var ratio = (point - origin - grabOffset - (grabOffset == 0 ? size / 2f : 0f)) / range;
        return Math.Clamp(ratio, 0, 1) * max;
    }

    private static bool Contains(UiRect rect, float x, float y) =>
        x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom;
}
