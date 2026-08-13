using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class SelectorPseudoTests
{
    private static ComputedStyle Compute(string css, Panel panel) => StyleSheet.Parse(css).Compute(panel);

    private static Panel PanelWithClass(string className, string tag = "div")
    {
        var panel = new Panel { TagName = tag };
        panel.AddClass(className);
        return panel;
    }

    [Fact]
    public void NotNegatesASimpleSelector()
    {
        var style = Compute(".item:not(.box) { width: 10px; }", PanelWithClass("box"));
        Assert.Equal(CssLength.Undefined, style.Width);
        style = Compute(".item:not(.box) { width: 10px; }", PanelWithClass("item"));
        Assert.Equal(CssLength.Points(10), style.Width);
    }

    [Fact]
    public void FirstAndLastChildMatchSiblings()
    {
        var parent = new Panel();
        var first = new Panel { TagName = "li" };
        var middle = new Panel { TagName = "li" };
        var last = new Panel { TagName = "li" };
        parent.AddChild(first);
        parent.AddChild(middle);
        parent.AddChild(last);
        var sheet = StyleSheet.Parse(
            "li:first-child { width: 1px; } li:last-child { width: 2px; } li:nth-child(2) { width: 3px; }");
        Assert.Equal(CssLength.Points(1), sheet.Compute(first).Width);
        Assert.Equal(CssLength.Points(3), sheet.Compute(middle).Width);
        Assert.Equal(CssLength.Points(2), sheet.Compute(last).Width);
    }

    [Fact]
    public void NthChildFormulaMatchesOddPositions()
    {
        var parent = new Panel();
        var children = Enumerable.Range(0, 5).Select(_ =>
        {
            var child = new Panel { TagName = "li" };
            parent.AddChild(child);
            return child;
        }).ToArray();
        var sheet = StyleSheet.Parse("li:nth-child(2n+1) { width: 9px; }");
        Assert.Equal(CssLength.Points(9), sheet.Compute(children[0]).Width);
        Assert.Equal(CssLength.Undefined, sheet.Compute(children[1]).Width);
        Assert.Equal(CssLength.Points(9), sheet.Compute(children[2]).Width);
        Assert.Equal(CssLength.Undefined, sheet.Compute(children[3]).Width);
        Assert.Equal(CssLength.Points(9), sheet.Compute(children[4]).Width);
    }

    [Fact]
    public void OnlyChildAndEmptyMatchLeafPanels()
    {
        var parent = new Panel();
        var only = new Panel { TagName = "span" };
        parent.AddChild(only);
        var sheet = StyleSheet.Parse("span:only-child { width: 5px; } div:empty { width: 6px; }");
        Assert.Equal(CssLength.Points(5), sheet.Compute(only).Width);
        Assert.Equal(CssLength.Undefined, sheet.Compute(parent).Width);
        var leaf = new Panel { TagName = "div" };
        Assert.Equal(CssLength.Points(6), sheet.Compute(leaf).Width);
    }

    [Fact]
    public void FocusWithinMatchesFocusedDescendants()
    {
        var container = new Panel { TagName = "div" };
        var input = new Panel { TagName = "input" };
        container.AddChild(input);
        var sheet = StyleSheet.Parse("div:focus-within { width: 40px; }");
        Assert.Equal(CssLength.Undefined, sheet.Compute(container).Width);
        input.SetFocused(true);
        Assert.Equal(CssLength.Points(40), sheet.Compute(container).Width);
    }

    [Fact]
    public void EnabledAndDisabledMatchEnabledState()
    {
        var sheet = StyleSheet.Parse("input:enabled { width: 1px; } input:disabled { width: 2px; }");
        var input = new Panel { TagName = "input" };
        Assert.Equal(CssLength.Points(1), sheet.Compute(input).Width);
        input.IsEnabled = false;
        Assert.Equal(CssLength.Points(2), sheet.Compute(input).Width);
    }

    [Fact]
    public void AdjacentSiblingCombinatorRequiresImmediatePredecessor()
    {
        var parent = new Panel();
        var a = PanelWithClass("a");
        var b = PanelWithClass("b");
        var c = PanelWithClass("b");
        parent.AddChild(a);
        parent.AddChild(b);
        parent.AddChild(c);
        var sheet = StyleSheet.Parse(".a + .b { width: 7px; }");
        // b is immediately preceded by .a → matches; c is preceded by .b → not.
        Assert.Equal(CssLength.Points(7), sheet.Compute(b).Width);
        Assert.Equal(CssLength.Undefined, sheet.Compute(c).Width);
    }

    [Fact]
    public void GeneralSiblingCombinatorMatchesAnyPredecessor()
    {
        var parent = new Panel();
        var a = PanelWithClass("a");
        var filler = PanelWithClass("filler");
        var b = PanelWithClass("b");
        parent.AddChild(a);
        parent.AddChild(filler);
        parent.AddChild(b);
        var sheet = StyleSheet.Parse(".a ~ .b { width: 8px; }");
        Assert.Equal(CssLength.Points(8), sheet.Compute(b).Width);
    }

    [Fact]
    public void MediaQueryGatesRulesByViewport()
    {
        var panel = PanelWithClass("box");
        var sheet = StyleSheet.Parse("@media (min-width: 800px) { .box { width: 50px; } }");
        sheet.SetViewport(1000, 500);
        Assert.Equal(CssLength.Points(50), sheet.Compute(panel).Width);
        sheet.SetViewport(500, 500);
        Assert.Equal(CssLength.Undefined, sheet.Compute(panel).Width);
    }

    [Fact]
    public void MediaQueryOrListMatchesAnyAlternative()
    {
        var panel = PanelWithClass("box");
        var sheet = StyleSheet.Parse("@media (max-width: 600px), (min-width: 1200px) { .box { width: 60px; } }");
        sheet.SetViewport(500, 400);
        Assert.Equal(CssLength.Points(60), sheet.Compute(panel).Width);
        sheet.SetViewport(1300, 400);
        Assert.Equal(CssLength.Points(60), sheet.Compute(panel).Width);
        sheet.SetViewport(800, 400);
        Assert.Equal(CssLength.Undefined, sheet.Compute(panel).Width);
    }

    [Fact]
    public void PseudoBeforeContentAndStyleAreComputed()
    {
        var panel = PanelWithClass("tag");
        var sheet = StyleSheet.Parse(".tag::before { content: \"→ \"; color: #ff0000; font-weight: bold; }");
        Assert.True(sheet.TryComputePseudo(panel, "before", new ComputedStyle(), out var content, out var style));
        Assert.Equal("→ ", content);
        Assert.Equal(700, style.FontWeight);
        Assert.Equal(new UiColor(255, 0, 0, 255), style.Color);
        Assert.False(sheet.TryComputePseudo(panel, "after", new ComputedStyle(), out _, out _));
    }

    [Fact]
    public void PseudoAttrContentReadsPanelAttribute()
    {
        var panel = new Panel { TagName = "button" };
        panel.Attributes["data-badge"] = "42";
        var sheet = StyleSheet.Parse("button::after { content: attr(data-badge); }");
        Assert.True(sheet.TryComputePseudo(panel, "after", new ComputedStyle(), out var content, out _));
        Assert.Equal("42", content);
    }

    [Fact]
    public void CascadePopulatesPseudoContentOnPanels()
    {
        using var ui = TestUi.Create();
        var panel = new Panel { TagName = "div" };
        panel.AddClass("badge");
        ui.Screen.AddChild(panel);
        ui.LoadStyles(".badge::after { content: \" ✓\"; color: #00aa00; }");
        ui.Prepare();
        Assert.NotNull(panel.PseudoAfter);
        Assert.Equal(" ✓", panel.PseudoAfter!.Value.Text);
        Assert.Equal(new UiColor(0, 170, 0, 255), panel.PseudoAfter.Value.Style.Color);
        Assert.Null(panel.PseudoBefore);
    }

    [Fact]
    public void TypographyPropertiesParse()
    {
        var panel = PanelWithClass("t");
        var style = Compute(
            ".t { font-family: Arial; font-weight: bold; letter-spacing: 2px; text-transform: uppercase;" +
            " white-space: nowrap; text-decoration: underline; text-overflow: ellipsis; }", panel);
        Assert.Equal("Arial", style.FontFamily);
        Assert.Equal(700, style.FontWeight);
        Assert.Equal(2, style.LetterSpacing);
        Assert.Equal("uppercase", style.TextTransform);
        Assert.Equal("nowrap", style.WhiteSpace);
        Assert.Equal("underline", style.TextDecoration);
        Assert.Equal("ellipsis", style.TextOverflow);
    }

    [Fact]
    public void FontWeightAcceptsKeywordsAndSteps()
    {
        var panel = PanelWithClass("t");
        Assert.Equal(700, Compute(".t { font-weight: bold; }", panel).FontWeight);
        Assert.Equal(400, Compute(".t { font-weight: normal; }", panel).FontWeight);
        Assert.Equal(900, Compute(".t { font-weight: 900; }", panel).FontWeight);
        // `lighter` steps down from the current weight (700 → 600) when the
        // rules cascade in order.
        var stepped = Compute(".t { font-weight: 700; } .t { font-weight: lighter; }", panel);
        Assert.Equal(600, stepped.FontWeight);
    }

    [Fact]
    public void NegativeLetterSpacingIsAccepted()
    {
        var panel = PanelWithClass("t");
        var style = Compute(".t { letter-spacing: -1px; }", panel);
        Assert.Equal(-1, style.LetterSpacing);
    }
}
