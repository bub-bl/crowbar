using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class StyleSheetTests
{
    private static ComputedStyle Compute(string css, Panel panel) => StyleSheet.Parse(css).Compute(panel);

    private static Panel PanelWithClass(string className, string tag = "div")
    {
        var panel = new Panel { TagName = tag };
        panel.AddClass(className);
        return panel;
    }

    [Fact]
    public void MatchingRuleAppliesProperties()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { width: 100px; height: 40px; background-color: #ff0000; }", panel);
        Assert.Equal(CssLength.Points(100), style.Width);
        Assert.Equal(CssLength.Points(40), style.Height);
        Assert.Equal(new UiColor(255, 0, 0, 255), style.BackgroundColor);
    }

    [Fact]
    public void NonMatchingRuleIsIgnored()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".other { width: 100px; }", panel);
        Assert.Equal(CssLength.Undefined, style.Width);
    }

    [Fact]
    public void UnknownPropertyIsIgnored()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { made-up-property: 12px; width: 20px; }", panel);
        Assert.Equal(CssLength.Points(20), style.Width);
    }

    [Fact]
    public void InvalidValueLeavesPropertyUnchanged()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { width: not-a-length; }", panel);
        Assert.Equal(CssLength.Undefined, style.Width);
    }

    [Fact]
    public void LaterRulesWinOverEarlierOnes()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { width: 10px; } .box { width: 20px; }", panel);
        Assert.Equal(CssLength.Points(20), style.Width);
    }

    [Fact]
    public void InlineStyleWinsOverRules()
    {
        var panel = PanelWithClass("box");
        panel.SetInlineStyle("width", "30px");
        var style = Compute(".box { width: 10px; }", panel);
        Assert.Equal(CssLength.Points(30), style.Width);
    }

    [Fact]
    public void DescendantSelectorMatchesNestedPanel()
    {
        var root = new Panel();
        var container = new Panel { TagName = "div" };
        container.AddClass("container");
        var child = PanelWithClass("child");
        container.AddChild(child);
        root.AddChild(container);

        var style = Compute(".container .child { width: 50px; }", child);
        Assert.Equal(CssLength.Points(50), style.Width);
    }

    [Fact]
    public void ChildSelectorDoesNotMatchIndirectDescendant()
    {
        var root = new Panel();
        var container = new Panel();
        container.AddClass("container");
        var middle = new Panel();
        middle.AddClass("middle");
        var child = PanelWithClass("child");
        middle.AddChild(child);
        container.AddChild(middle);
        root.AddChild(container);

        var style = Compute(".container > .child { width: 50px; }", child);
        Assert.Equal(CssLength.Undefined, style.Width);
    }

    [Fact]
    public void IdAndAttributeSelectorsMatch()
    {
        var panel = PanelWithClass("box");
        panel.Id = "main";
        panel.Attributes["data-kind"] = "primary";
        var style = Compute("#main[data-kind=primary] { width: 40px; }", panel);
        Assert.Equal(CssLength.Points(40), style.Width);
    }

    [Fact]
    public void TagSelectorMatchesTagName()
    {
        var panel = PanelWithClass("btn");
        panel.TagName = "button";
        var style = Compute("button.btn { height: 20px; }", panel);
        Assert.Equal(CssLength.Points(20), style.Height);
    }

    [Fact]
    public void MarginShorthandExpandsToFourSides()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { margin: 10px 20px 30px 40px; }", panel);
        Assert.Equal(CssLength.Points(10), style.MarginTop);
        Assert.Equal(CssLength.Points(20), style.MarginRight);
        Assert.Equal(CssLength.Points(30), style.MarginBottom);
        Assert.Equal(CssLength.Points(40), style.MarginLeft);
    }

    [Fact]
    public void MarginTwoValueShorthandRepeats()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { margin: 5px 10px; }", panel);
        Assert.Equal(CssLength.Points(5), style.MarginTop);
        Assert.Equal(CssLength.Points(10), style.MarginRight);
        Assert.Equal(CssLength.Points(5), style.MarginBottom);
        Assert.Equal(CssLength.Points(10), style.MarginLeft);
    }

    [Fact]
    public void PaddingIndividualSidesApply()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { padding-left: 12px; padding-right: 4px; }", panel);
        Assert.Equal(CssLength.Points(12), style.PaddingLeft);
        Assert.Equal(CssLength.Points(4), style.PaddingRight);
        Assert.Equal(CssLength.Undefined, style.PaddingTop);
    }

    [Fact]
    public void GapShorthandAppliesRowAndColumn()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { gap: 8px; }", panel);
        Assert.Equal(CssLength.Points(8), style.Gap);
        Assert.Equal(CssLength.Points(8), style.RowGap);
        Assert.Equal(CssLength.Points(8), style.ColumnGap);
    }

    [Fact]
    public void PercentLengthsKeepTheirUnit()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { width: 50%; margin-top: 10%; }", panel);
        Assert.Equal(CssLength.Percent(50), style.Width);
        Assert.Equal(CssLength.Percent(10), style.MarginTop);
    }

    [Fact]
    public void AutoMarginParses()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { margin: auto; }", panel);
        Assert.Equal(CssLength.Auto, style.MarginTop);
        Assert.Equal(CssLength.Auto, style.MarginLeft);
    }

    [Fact]
    public void ContentSizingKeywordsParse()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { width: max-content; height: fit-content; }", panel);
        Assert.Equal(CssLength.MaxContent, style.Width);
        Assert.Equal(CssLength.FitContent, style.Height);
    }

    [Fact]
    public void PaddingRejectsAuto()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { padding: auto; }", panel);
        Assert.Equal(CssLength.Undefined, style.PaddingTop);
    }

    [Fact]
    public void FlexShorthandSingleNumberExpands()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { flex: 1; }", panel);
        Assert.Equal(1, style.FlexGrow);
        Assert.Equal(1, style.FlexShrink);
        Assert.Equal(CssLength.Points(0), style.FlexBasis);
    }

    [Fact]
    public void FlexShorthandNoneZeroesGrowAndShrink()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { flex: none; }", panel);
        Assert.Equal(0, style.FlexGrow);
        Assert.Equal(0, style.FlexShrink);
        Assert.Equal(CssLength.Auto, style.FlexBasis);
    }

    [Fact]
    public void FlexShorthandThreeValuesExpands()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { flex: 2 1 10%; }", panel);
        Assert.Equal(2, style.FlexGrow);
        Assert.Equal(1, style.FlexShrink);
        Assert.Equal(CssLength.Percent(10), style.FlexBasis);
    }

    [Fact]
    public void KeywordPropertiesValidateTheirValues()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { align-self: bogus; flex-wrap: wrap; position: absolute; direction: rtl; display: contents; }", panel);
        Assert.Equal("auto", style.AlignSelf); // invalid keyword ignored
        Assert.Equal("wrap", style.FlexWrap);
        Assert.Equal("absolute", style.PositionType);
        Assert.Equal("rtl", style.Direction);
        Assert.Equal("contents", style.Display);
    }

    [Fact]
    public void FlexBasisAndAspectRatioParse()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { flex-basis: 120px; aspect-ratio: 1.5; }", panel);
        Assert.Equal(CssLength.Points(120), style.FlexBasis);
        Assert.Equal(1.5f, style.AspectRatio);
    }

    [Fact]
    public void BorderShorthandExtractsWidth()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { border: 1px solid #cccccc; }", panel);
        Assert.Equal(CssLength.Points(1), style.BorderTop);
        Assert.Equal(CssLength.Points(1), style.BorderRight);
        Assert.Equal(CssLength.Points(1), style.BorderBottom);
        Assert.Equal(CssLength.Points(1), style.BorderLeft);
    }

    [Fact]
    public void PositionOffsetsParse()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { top: 10px; left: 20%; }", panel);
        Assert.Equal(CssLength.Points(10), style.PositionTop);
        Assert.Equal(CssLength.Percent(20), style.PositionLeft);
        Assert.Equal(CssLength.Undefined, style.PositionRight);
    }

    [Fact]
    public void TransitionShorthandParses()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { transition: background-color 0.3s ease; }", panel);
        var transition = Assert.Single(style.Transitions);
        Assert.Equal("background-color", transition.Property);
        Assert.Equal(0.3f, transition.Duration);
        Assert.Equal("ease", transition.TimingFunction);
    }

    [Fact]
    public void PseudoClassesMatchInteractiveState()
    {
        var panel = PanelWithClass("btn");
        var hoverSheet = StyleSheet.Parse(".btn:hover { background-color: #00ff00; }");
        Assert.Equal(new UiColor(0, 0, 0, 0), hoverSheet.Compute(panel).BackgroundColor);
        panel.SetHovered(true);
        Assert.Equal(new UiColor(0, 255, 0, 255), hoverSheet.Compute(panel).BackgroundColor);
    }

    [Fact]
    public void ScopedSelectorAppendsScopeAttributeToEveryCompoundSelector()
    {
        Assert.Equal(".btn[b-myc]:hover", StyleSheet.ScopeSelector(".btn:hover", "b-myc"));
        Assert.Equal("div[b-myc] > .btn[b-myc]", StyleSheet.ScopeSelector("div > .btn", "b-myc"));
    }

    [Fact]
    public void DeepSelectorUnscopesTheTail()
    {
        Assert.Equal(".root[b-myc] .child", StyleSheet.ScopeSelector(".root ::deep .child", "b-myc"));
    }

    [Fact]
    public void ParseHandlesCommentsFreeCssAndMultipleRules()
    {
        var sheet = StyleSheet.Parse(".a { width: 1px; } .b { width: 2px; }");
        Assert.Equal(2, sheet.Rules.Count);
        Assert.Equal(".a", sheet.Rules[0].Selector);
        Assert.Equal(".b", sheet.Rules[1].Selector);
    }
}
