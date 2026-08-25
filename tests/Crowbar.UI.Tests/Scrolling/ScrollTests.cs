using Crowbar.UI;

namespace Crowbar.UI.Tests.Scrolling;

public class ScrollTests
{
    [Fact]
    public void OverflowKeywordsParse()
    {
        var style = new ComputedStyle();
        Assert.Equal("visible", style.Overflow);

        Assert.True(CssProperties.TryApply(style, "overflow", "auto"));
        Assert.Equal("auto", style.Overflow);
        Assert.True(CssProperties.TryApply(style, "overflow", "clip"));
        Assert.Equal("clip", style.Overflow);
        Assert.True(CssProperties.TryApply(style, "overflow", "scroll"));
        Assert.Equal("scroll", style.Overflow);
        Assert.True(CssProperties.TryApply(style, "overflow", "hidden"));
        Assert.Equal("hidden", style.Overflow);

        // Unknown values are ignored, mirroring CSS.
        Assert.False(CssProperties.TryApply(style, "overflow", "bogus"));
        Assert.Equal("hidden", style.Overflow);
    }

    [Fact]
    public void ScrollbarStylePropertiesParse()
    {
        var style = new ComputedStyle();
        // Defaults mirror the engine's hardcoded look.
        Assert.Equal(5, style.ScrollbarRadius);
        Assert.Equal(0, style.ScrollbarWidth);
        Assert.Equal(new UiColor(150, 172, 205, 215), style.ScrollbarThumbColor);
        Assert.Equal(new UiColor(15, 24, 40, 110), style.ScrollbarTrackColor);

        Assert.True(CssProperties.TryApply(style, "scrollbar-color", "#ff0000 #00ff00"));
        Assert.Equal(new UiColor(255, 0, 0, 255), style.ScrollbarThumbColor);
        Assert.Equal(new UiColor(0, 255, 0, 255), style.ScrollbarTrackColor);

        // auto resets to the engine defaults.
        Assert.True(CssProperties.TryApply(style, "scrollbar-color", "auto"));
        Assert.Equal(new UiColor(150, 172, 205, 215), style.ScrollbarThumbColor);
        Assert.Equal(new UiColor(15, 24, 40, 110), style.ScrollbarTrackColor);

        Assert.True(CssProperties.TryApply(style, "scrollbar-width", "auto"));
        Assert.Equal(0, style.ScrollbarWidth);
        Assert.True(CssProperties.TryApply(style, "scrollbar-width", "thin"));
        Assert.Equal(6, style.ScrollbarWidth);
        Assert.True(CssProperties.TryApply(style, "scrollbar-width", "14px"));
        Assert.Equal(14, style.ScrollbarWidth);

        Assert.True(CssProperties.TryApply(style, "scrollbar-radius", "2px"));
        Assert.Equal(2, style.ScrollbarRadius);

        // Invalid values are ignored, mirroring CSS.
        Assert.False(CssProperties.TryApply(style, "scrollbar-color", "red"));
        Assert.False(CssProperties.TryApply(style, "scrollbar-width", "bogus"));
        Assert.Equal(14, style.ScrollbarWidth);
    }

    [Fact]
    public void ScrollbarWidthStylesTheScrollbarGeometry()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.AddClass("scroll");
        ui.Screen.AddChild(container);
        ui.LoadStyles(".scroll { width: 200px; height: 100px; overflow: auto; scrollbar-width: 14px; }");
        ui.Prepare();

        Assert.Equal(14, container.ScrollbarThickness);
        var track = ScrollBars.VerticalTrack(container);
        Assert.Equal(14, track.Width);
        Assert.Equal(container.Layout.Right - 14, track.X);

        // Default (auto) keeps the engine thickness.
        var plain = new Panel { TagName = "div" };
        plain.SetInlineStyle("width", "200px");
        plain.SetInlineStyle("height", "100px");
        plain.SetInlineStyle("overflow", "auto");
        ui.Screen.AddChild(plain);
        ui.Prepare();
        Assert.Equal(ScrollBars.Thickness, plain.ScrollbarThickness);
    }

    [Fact]
    public void ScrollRangeComputedFromOverflowingChildren()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        // 6 children of 40px in a 100px-tall container: 140px are scrollable.
        Assert.Equal(140, container.MaxScrollY);
        Assert.Equal(0, container.MaxScrollX);
        Assert.True(container.CanScrollVertically);
    }

    [Fact]
    public void ScrollToClampsToScrollableRange()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        container.ScrollTo(0, 500);
        Assert.Equal(140, container.ScrollY);

        container.ScrollBy(0, 30);
        Assert.Equal(140, container.ScrollY); // already at the bottom

        container.ScrollBy(0, -10);
        Assert.Equal(130, container.ScrollY);

        container.ScrollTo(0, -50);
        Assert.Equal(0, container.ScrollY);
    }

    [Fact]
    public void ScrollRangeComputedFromChildMargins()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.AddClass("scroll");
        ui.Screen.AddChild(container);
        var child = new Panel { TagName = "div" };
        child.SetInlineStyle("height", "40px");
        child.SetInlineStyle("margin-top", "120px");
        child.SetInlineStyle("flex-shrink", "0");
        container.AddChild(child);
        ui.LoadStyles(".scroll { width: 200px; height: 100px; overflow: auto; }");
        ui.Prepare();

        // The 120px top margin is part of the scrollable overflow: 160 - 100.
        Assert.Equal(60, container.MaxScrollY);
    }

    [Fact]
    public void AutoShowsScrollbarOnlyWhenOverflowing()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();
        Assert.True(ScrollBars.ShouldShowVertical(container));

        // Same container without overflowing content: no scrollbar.
        var fits = new Panel { TagName = "div" };
        fits.SetInlineStyle("width", "200px");
        fits.SetInlineStyle("height", "100px");
        fits.SetInlineStyle("overflow", "auto");
        ui.Screen.AddChild(fits);
        ui.Prepare();
        Assert.False(ScrollBars.ShouldShowVertical(fits));
        Assert.False(fits.CanScrollVertically);
    }

    [Fact]
    public void ScrollAlwaysShowsScrollbarEvenWhenContentFits()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.SetInlineStyle("width", "200px");
        container.SetInlineStyle("height", "100px");
        container.SetInlineStyle("overflow", "scroll");
        ui.Screen.AddChild(container);
        ui.Prepare();

        Assert.True(ScrollBars.ShouldShowVertical(container));
        Assert.Equal(0, container.MaxScrollY);
    }

    [Fact]
    public void HiddenAndClipDoNotScroll()
    {
        foreach (var overflow in new[] { "hidden", "clip" })
        {
            using var ui = TestUi.Create();
            var container = NewScrollContainer(overflow);
            ui.Screen.AddChild(container);
            ui.Prepare();

            Assert.False(container.IsScrollContainer);
            Assert.False(container.CanScrollVertically);
            Assert.False(ScrollBars.ShouldShowVertical(container));
            Assert.True(container.ClipsContent);

            ui.ProcessPointerWheel(container.Layout.X + 50, container.Layout.Y + 50, 0, -1);
            Assert.Equal(0, container.ScrollY);
        }
    }

    [Fact]
    public void WheelScrollsNearestScrollableAncestor()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        var x = container.Layout.X + 50;
        var y = container.Layout.Y + 50;

        ui.ProcessPointerWheel(x, y, 0, -1); // wheel down → scrolls down
        Assert.Equal(40, container.ScrollY);

        ui.ProcessPointerWheel(x, y, 0, 1); // wheel up → scrolls up
        Assert.Equal(0, container.ScrollY);

        // Horizontal wheel scrolls horizontally when the panel can.
        ui.ProcessPointerWheel(x, y, -1, 0);
        Assert.Equal(0, container.ScrollX); // no horizontal overflow
    }

    [Fact]
    public void WheelPrefersInnerScrollableContainer()
    {
        using var ui = TestUi.Create();
        var outer = new Panel { TagName = "div" };
        outer.SetInlineStyle("width", "300px");
        outer.SetInlineStyle("height", "150px");
        outer.SetInlineStyle("overflow", "auto");
        ui.Screen.AddChild(outer);

        var inner = NewScrollContainer(overflow: "auto", width: 200, height: 60);
        inner.SetInlineStyle("flex-shrink", "0"); // keep its 60px height inside the outer column
        outer.AddChild(inner);
        // Another tall child makes the outer container scrollable too.
        var tail = new Panel { TagName = "div" };
        tail.SetInlineStyle("height", "300px");
        tail.SetInlineStyle("flex-shrink", "0");
        outer.AddChild(tail);
        ui.Prepare();

        Assert.True(outer.MaxScrollY > 0);
        Assert.Equal(180, inner.MaxScrollY); // 6 × 40px content in a 60px box

        // Wheel over the inner container's content scrolls the inner one only.
        ui.ProcessPointerWheel(inner.Layout.X + 20, inner.Layout.Y + 20, 0, -1);
        Assert.Equal(40, inner.ScrollY);
        Assert.Equal(0, outer.ScrollY);
    }

    [Fact]
    public void ScrolledContentIsHittable()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        var x = container.Layout.X + 50;
        var y = container.Layout.Y + 90;

        // Before scrolling, content y=90 is child[2] (80..120).
        Assert.Same(container.Children[2], ui.Screen.HitTest(x, y));

        container.ScrollTo(0, 50);
        // After scrolling 50px, content y=140 is child[3] (120..160).
        Assert.Same(container.Children[3], ui.Screen.HitTest(x, y));
    }

    [Fact]
    public void ChildrenOutsideClipAreNotHit()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.SetInlineStyle("width", "200px");
        container.SetInlineStyle("height", "100px");
        container.SetInlineStyle("overflow", "hidden");
        ui.Screen.AddChild(container);
        var visible = new Panel { TagName = "div" };
        visible.SetInlineStyle("height", "40px");
        visible.SetInlineStyle("flex-shrink", "0");
        container.AddChild(visible);
        var clipped = new Panel { TagName = "div" };
        clipped.SetInlineStyle("height", "40px");
        clipped.SetInlineStyle("margin-top", "160px");
        clipped.SetInlineStyle("flex-shrink", "0");
        container.AddChild(clipped);
        ui.Prepare();

        var x = container.Layout.X + 50;
        Assert.Same(visible, ui.Screen.HitTest(x, container.Layout.Y + 10));
        // The second child starts at content y=160, past the 100px clip.
        Assert.Same(container, ui.Screen.HitTest(x, container.Layout.Y + 90));
        // A pointer at the clipped child's visual position never reaches the
        // hidden child (or its container); the root screen is what is hit.
        var hit = ui.Screen.HitTest(x, container.Layout.Bottom + 70);
        Assert.NotSame(clipped, hit);
        Assert.NotSame(container, hit);
    }

    [Fact]
    public void VisibleOverflowChildrenRemainHittableOutsideThePanel()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.SetInlineStyle("width", "200px");
        container.SetInlineStyle("height", "100px");
        ui.Screen.AddChild(container);
        var child = new Panel { TagName = "div" };
        child.SetInlineStyle("height", "40px");
        child.SetInlineStyle("margin-top", "120px");
        child.SetInlineStyle("flex-shrink", "0");
        container.AddChild(child);
        ui.Prepare();

        // The child overflows the container (overflow: visible) and stays hittable.
        var hit = ui.Screen.HitTest(container.Layout.X + 50, container.Layout.Y + 140);
        Assert.Same(child, hit);
    }

    [Fact]
    public void LayeredOverlayChildRemainsHittableOutsideClippedContainer()
    {
        using var ui = TestUi.Create();
        var container = new Panel { TagName = "div" };
        container.SetInlineStyle("width", "200px");
        container.SetInlineStyle("height", "100px");
        container.SetInlineStyle("overflow", "hidden");
        ui.Screen.AddChild(container);
        var overlay = new Panel { TagName = "div" };
        overlay.SetInlineStyle("width", "80px");
        overlay.SetInlineStyle("height", "60px");
        overlay.SetInlineStyle("position", "absolute");
        overlay.SetInlineStyle("top", "120px");
        overlay.SetInlineStyle("left", "10px");
        overlay.SetInlineStyle("z-index", "10");
        container.AddChild(overlay);
        ui.Prepare();

        // The layering overlay (absolute + z-index) escapes the overflow:hidden
        // clip: although it is positioned below the container's box, it is still
        // the panel the pointer lands on and receives clicks.
        Assert.True(container.Layout.Bottom < overlay.Layout.Y, "the overlay should sit below the clipped container");
        Assert.Same(overlay, ui.Screen.HitTest(overlay.Layout.X + 30, overlay.Layout.Y + 10));
    }

    [Fact]
    public void ScrollbarDragScrollsTheContainer()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        var track = ScrollBars.VerticalTrack(container);
        var x = track.X + track.Width / 2;

        ui.ProcessPointerDown(x, track.Y + 2);
        Assert.Equal(0, container.ScrollY); // thumb snapped to the top

        ui.ProcessPointerMove(x, track.Bottom - 2);
        Assert.Equal(140, container.ScrollY); // thumb dragged to the bottom

        ui.ProcessPointerUp(x, track.Bottom - 2);
        // After release, moving the pointer no longer scrolls.
        ui.ProcessPointerMove(x, track.Y + 2);
        Assert.Equal(140, container.ScrollY);
    }

    [Fact]
    public void ScrollbarThumbGrabPreservesPointerOffset()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        // Scroll partway so the thumb sits mid-track.
        container.ScrollTo(0, 70);
        ui.Prepare();

        var track = ScrollBars.VerticalTrack(container);
        var thumb = ScrollBars.VerticalThumb(container);
        var x = track.X + track.Width / 2;
        // Grab a point 4px inside the thumb's lower edge.
        var grabY = thumb.Y + thumb.Height - 4;
        ui.ProcessPointerDown(x, grabY);

        // The grab offset is preserved the moment the thumb is pressed: it must
        // not recenter under the cursor (which would leave the thumb leading
        // edge 4px ahead of the grabbed point).
        Assert.Equal(70, container.ScrollY);

        // Moving 10px down advances the scroll by the exact proportional track
        // amount while keeping the same 4px grab anchor.
        ui.ProcessPointerMove(x, grabY + 10);
        ui.Prepare();
        Assert.Equal(94, container.ScrollY, 0.5);
    }

    [Fact]
    public void KeyboardArrowsScrollFocusedContainer()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        ui.ProcessPointerDown(container.Layout.X + 50, container.Layout.Y + 10);
        Assert.True(container.Children[0].IsFocused);

        ui.ProcessKey(0x28, true); // Down
        Assert.Equal(40, container.ScrollY);

        ui.ProcessKey(0x23, true); // End
        Assert.Equal(140, container.ScrollY);

        ui.ProcessKey(0x24, true); // Home
        Assert.Equal(0, container.ScrollY);
    }

    [Fact]
    public void ScrolledContentIsClippedAndTranslated()
    {
        using var ui = TestUi.Create(width: 320, height: 200);
        var container = new Panel { TagName = "div" };
        container.AddClass("scroll");
        ui.Screen.AddChild(container);
        var child = new Panel { TagName = "div" };
        child.AddClass("item");
        container.AddChild(child);
        ui.LoadStyles(
            ".scroll { width: 120px; height: 80px; overflow: auto; } " +
            ".item { width: 100px; height: 40px; margin-top: 120px; flex-shrink: 0; background-color: #ff0000; }");
        ui.Prepare();

        // The child sits below the fold: the container clips and scrolls.
        Assert.True(container.ClipsContent);
        Assert.Equal(80, container.MaxScrollY);
        Assert.Equal(120, child.Layout.Y);

        container.ScrollTo(0, 60);
        Assert.Equal(60, container.ScrollY);
        Assert.Equal(80, container.MaxScrollY);
    }

    [Fact]
    public void EditorDemoPageRendersScrollableArea()
    {
        // End-to-end: compile the shipped demo page (markup + scoped CSS +
        // child components) and exercise the scroll example the same way a user
        // would, with the wheel and the scrollbar thumb.
        using var ui = TestUi.Create(width: 1280, height: 720);
        var uiDirectory = Path.Combine(FindRepoRoot(), "src", "Editor", "Ui");
        ui.RegisterRazorComponentsFromDirectory(uiDirectory);
        ui.Navigate("/");
        ui.Prepare();

        var scrollArea = TestUi.Find(ui.Screen, p => p.Classes.Contains("scroll-area"));
        Assert.NotNull(scrollArea);
        Assert.Equal(24, scrollArea.Children.Count);
        Assert.True(scrollArea.MaxScrollY > 0);
        Assert.True(ScrollBars.ShouldShowVertical(scrollArea));

        // Mouse wheel over the area scrolls it.
        ui.ProcessPointerWheel(scrollArea.Layout.X + 50, scrollArea.Layout.Y + 50, 0, -1);
        Assert.Equal(40, scrollArea.ScrollY);

        // Dragging the thumb to the bottom of the track scrolls to the end.
        var track = ScrollBars.VerticalTrack(scrollArea);
        var x = track.X + track.Width / 2;
        ui.ProcessPointerDown(x, track.Bottom - 2);
        Assert.Equal(scrollArea.MaxScrollY, scrollArea.ScrollY);
        ui.ProcessPointerUp(x, track.Bottom - 2);

        // The overflow:hidden example clips its oversized child.
        var clipDemo = TestUi.Find(ui.Screen, p => p.Classes.Contains("clip-demo"));
        Assert.NotNull(clipDemo);
        Assert.True(clipDemo.ClipsContent);
        Assert.False(clipDemo.CanScrollHorizontally);
        Assert.False(ScrollBars.ShouldShowHorizontal(clipDemo));
    }

    [Fact]
    public void ScrollPositionSurvivesRelayout()
    {
        using var ui = TestUi.Create();
        var container = NewScrollContainer(overflow: "auto");
        ui.Screen.AddChild(container);
        ui.Prepare();

        container.ScrollTo(0, 100);
        ui.Prepare(); // full re-layout must not reset the offset
        Assert.Equal(100, container.ScrollY);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Editor", "Ui", "Demo.razor")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static Panel NewScrollContainer(string overflow, float width = 200, float height = 100)
    {
        var container = new Panel { TagName = "div" };
        container.SetInlineStyle("width", width + "px");
        container.SetInlineStyle("height", height + "px");
        container.SetInlineStyle("overflow", overflow);
        for (var i = 0; i < 6; i++)
        {
            var child = new Panel { TagName = "div" };
            child.SetInlineStyle("height", "40px");
            child.SetInlineStyle("flex-shrink", "0");
            container.AddChild(child);
        }

        return container;
    }
}
