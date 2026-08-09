using Crowbar.UI;

namespace Crowbar.UI.Tests.Styling;

public class AnimationTests
{
    private static ComputedStyle Compute(string css, Panel panel) => StyleSheet.Parse(css).Compute(panel);

    private static Panel PanelWithClass(string className, string tag = "div")
    {
        var panel = new Panel { TagName = tag };
        panel.AddClass(className);
        return panel;
    }

    // ---- @keyframes parsing -------------------------------------------------

    [Fact]
    public void KeyframesParseFromStylesheet()
    {
        Keyframes.Clear();
        StyleSheet.Parse("@keyframes fade { from { opacity: 0; } to { opacity: 1; } } .box { width: 10px; }");

        Assert.True(Keyframes.TryGet("fade", out var keyframes));
        Assert.Equal(2, keyframes!.Frames.Count);
        Assert.Equal(0f, keyframes.Frames[0].Offset);
        Assert.Equal(1f, keyframes.Frames[1].Offset);
        Assert.Equal("0", keyframes.Frames[0].Declarations["opacity"]);
    }

    [Fact]
    public void KeyframesWithCommaSelectorsExpandToMultipleFrames()
    {
        Keyframes.Clear();
        StyleSheet.Parse("@keyframes pulse { 0%, 100% { opacity: 0.5; } 50% { opacity: 1; } }");
        Assert.True(Keyframes.TryGet("pulse", out var keyframes));
        Assert.Equal(3, keyframes!.Frames.Count);
        Assert.Equal(0f, keyframes.Frames[0].Offset);
        Assert.Equal(0.5f, keyframes.Frames[1].Offset);
        Assert.Equal(1f, keyframes.Frames[2].Offset);
        Assert.Equal("0.5", keyframes.Frames[0].Declarations["opacity"]);
        Assert.Equal("0.5", keyframes.Frames[2].Declarations["opacity"]);
    }

    [Fact]
    public void KeyframesBlocksDoNotBecomeStyleRules()
    {
        Keyframes.Clear();
        var sheet = StyleSheet.Parse(
            "@keyframes pulse { 0% { opacity: 0.5; } 50% { opacity: 1; } 100% { opacity: 0.5; } } .box { width: 10px; }");
        Assert.Single(sheet.Rules);
        Assert.Equal(".box", sheet.Rules[0].Selector);
        Assert.True(Keyframes.TryGet("pulse", out var keyframes));
        Assert.Equal(3, keyframes!.Frames.Count);
        Assert.Equal(0.5f, keyframes.Frames[1].Offset);
    }

    [Fact]
    public void ScopedKeyframesAreRenamedInDefinitionAndUsage()
    {
        Keyframes.Clear();
        var sheet = StyleSheet.Parse(
            "@keyframes fade { from { opacity: 0; } to { opacity: 1; } } .box { animation: fade 1s; }", "b-root");

        Assert.True(Keyframes.TryGet("b-root-fade", out _));
        Assert.False(Keyframes.TryGet("fade", out _));
        Assert.Contains("b-root-fade", sheet.Rules[0].Properties["animation"]);
    }

    // ---- property parsing ---------------------------------------------------

    [Fact]
    public void AnimationShorthandParsesEveryDescriptor()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { animation: fade 1s ease-in 0.5s 2 alternate both paused; }", panel);
        Assert.Equal("fade", style.AnimationName);
        Assert.Equal(1f, style.AnimationDuration);
        Assert.Equal("ease-in", style.AnimationTimingFunction);
        Assert.Equal(2f, style.AnimationIterationCount);
        Assert.Equal("alternate", style.AnimationDirection);
        Assert.Equal(0.5f, style.AnimationDelay);
        Assert.Equal("both", style.AnimationFillMode);
        Assert.Equal("paused", style.AnimationPlayState);
    }

    [Fact]
    public void AnimationShorthandWithStepsAndInfiniteIterations()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { animation: fade 0.5s steps(4, end) infinite; }", panel);
        Assert.Equal("fade", style.AnimationName);
        Assert.Equal(0.5f, style.AnimationDuration);
        Assert.Equal("steps(4, end)", style.AnimationTimingFunction);
        Assert.True(float.IsPositiveInfinity(style.AnimationIterationCount));
    }

    [Fact]
    public void AnimationNoneClearsTheAnimation()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { animation: none; }", panel);
        Assert.Equal("none", style.AnimationName);
        Assert.Equal(0f, style.AnimationDuration);
    }

    [Fact]
    public void AnimationLonghandsApplyIndividually()
    {
        var panel = PanelWithClass("box");
        var style = Compute(
            ".box { animation-name: spin; animation-duration: 2s; animation-iteration-count: infinite; animation-direction: reverse; animation-delay: -0.5s; }",
            panel);
        Assert.Equal("spin", style.AnimationName);
        Assert.Equal(2f, style.AnimationDuration);
        Assert.True(float.IsPositiveInfinity(style.AnimationIterationCount));
        Assert.Equal("reverse", style.AnimationDirection);
        Assert.Equal(-0.5f, style.AnimationDelay);
    }

    [Fact]
    public void TransitionShorthandParsesDelayInEitherOrder()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { transition: opacity 0.2s ease 0.1s; }", panel);
        Assert.Equal("opacity", style.TransitionProperty);
        Assert.Equal(0.2f, style.TransitionDuration);
        Assert.Equal("ease", style.TransitionTimingFunction);
        Assert.Equal(0.1f, style.TransitionDelay);

        var swapped = Compute(".box { transition: opacity 0.2s 0.1s linear; }", panel);
        Assert.Equal(0.2f, swapped.TransitionDuration);
        Assert.Equal(0.1f, swapped.TransitionDelay);
        Assert.Equal("linear", swapped.TransitionTimingFunction);
    }

    [Fact]
    public void TransformShorthandParsesComponents()
    {
        var panel = PanelWithClass("box");
        var style = Compute(".box { transform: translate(10px, 20px) rotate(45deg) scale(1.5); }", panel);
        Assert.Equal(10f, style.TranslateX);
        Assert.Equal(20f, style.TranslateY);
        Assert.Equal(45f, style.Rotate);
        Assert.Equal(1.5f, style.ScaleX);
        Assert.Equal(1.5f, style.ScaleY);
        Assert.True(style.HasTransform);

        var none = Compute(".box { transform: none; }", panel);
        Assert.False(none.HasTransform);
    }

    // ---- keyframe sampling --------------------------------------------------

    [Fact]
    public void SampleInterpolatesBetweenSurroundingKeyframes()
    {
        Keyframes.Clear();
        var keyframes = Keyframes.Define("tri",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(0.5f, ("opacity", "1")),
            KeyframeFrame.At(1f, ("opacity", "0.5")));
        var baseStyle = new ComputedStyle { Opacity = 0.3f };

        Assert.Equal(0f, Keyframes.Sample(baseStyle, keyframes, 0f).Opacity, 3);
        Assert.Equal(0.5f, Keyframes.Sample(baseStyle, keyframes, 0.25f).Opacity, 3);
        Assert.Equal(1f, Keyframes.Sample(baseStyle, keyframes, 0.5f).Opacity, 3);
        Assert.Equal(0.75f, Keyframes.Sample(baseStyle, keyframes, 0.75f).Opacity, 3);
        Assert.Equal(0.5f, Keyframes.Sample(baseStyle, keyframes, 1f).Opacity, 3);
    }

    [Fact]
    public void SampleHoldsValuesOfUnspecifiedKeyframes()
    {
        Keyframes.Clear();
        var keyframes = Keyframes.Define("hold",
            KeyframeFrame.At(0f, ("opacity", "0.2")),
            KeyframeFrame.At(0.5f, ("background-color", "#ff0000")),
            KeyframeFrame.At(1f, ("opacity", "0.8")));
        var baseStyle = new ComputedStyle { Opacity = 0.5f };

        // opacity is only declared at 0% and 100%: it holds 0.2 through the
        // whole animation and only snaps to 0.8 at the very end.
        Assert.Equal(0.2f, Keyframes.Sample(baseStyle, keyframes, 0.25f).Opacity, 3);
        Assert.Equal(0.2f, Keyframes.Sample(baseStyle, keyframes, 0.75f).Opacity, 3);
        Assert.Equal(0.8f, Keyframes.Sample(baseStyle, keyframes, 1f).Opacity, 3);
        Assert.Equal(new UiColor(255, 0, 0, 255), Keyframes.Sample(baseStyle, keyframes, 0.75f).BackgroundColor);
    }

    [Fact]
    public void SampleExpandsTransformShorthandIntoComponents()
    {
        Keyframes.Clear();
        var keyframes = Keyframes.Define("slide-short",
            KeyframeFrame.At(0f, ("transform", "translate(0px, 0px)")),
            KeyframeFrame.At(1f, ("transform", "translate(100px, 0px)")));
        var baseStyle = new ComputedStyle();
        Assert.Equal(0f, Keyframes.Sample(baseStyle, keyframes, 0f).TranslateX, 3);
        Assert.Equal(50f, Keyframes.Sample(baseStyle, keyframes, 0.5f).TranslateX, 3);
        Assert.Equal(100f, Keyframes.Sample(baseStyle, keyframes, 1f).TranslateX, 3);
    }

    [Fact]
    public void PanelAnimationWithTransformShorthand()
    {
        Keyframes.Clear();
        Keyframes.Define("slide-rotate",
            KeyframeFrame.At(0f, ("transform", "translate(0px, 0px) rotate(0deg)")),
            KeyframeFrame.At(1f, ("transform", "translate(40px, 0px) rotate(180deg)")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "slide-rotate 1s linear");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.5f);
        Assert.Equal(20f, panel.ComputedStyle.TranslateX, 3);
        Assert.Equal(90f, panel.ComputedStyle.Rotate, 3);
    }

    [Fact]
    public void SampleAppliesSingleKeyframeOutright()
    {
        Keyframes.Clear();
        var keyframes = Keyframes.Define("one", KeyframeFrame.At(0.5f, ("opacity", "0.4")));
        var baseStyle = new ComputedStyle { Opacity = 1f };
        Assert.Equal(0.4f, Keyframes.Sample(baseStyle, keyframes, 0f).Opacity, 3);
        Assert.Equal(0.4f, Keyframes.Sample(baseStyle, keyframes, 1f).Opacity, 3);
    }

    // ---- the panel animation driver ----------------------------------------

    [Fact]
    public void PanelRunsKeyframeAnimationAndRevertsOnCompletion()
    {
        Keyframes.Clear();
        Keyframes.Define("fade",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade 1s linear");
        panel.SetInlineStyle("opacity", "0.5"); // resting style, overridden by the animation
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3); // from keyframe

        ui.Update(0.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);

        // The iteration completes and, with fill mode none, the element snaps
        // back to its resting style immediately.
        ui.Update(0.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void AnimationFillForwardsKeepsTheEndState()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-f",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-f 1s linear forwards");
        panel.SetInlineStyle("opacity", "0.5");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(1f);
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3); // kept after completion
    }

    [Fact]
    public void FilledAnimationSurvivesLaterRendersAndTicks()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-fr",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-fr 1s linear forwards");
        panel.SetInlineStyle("opacity", "0.5");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(1f);
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3); // filled end state

        // Further renders and ticks must not restart the animation.
        ui.Render();
        ui.Update(0.5f);
        ui.Render();
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void AnimationIterationCountRunsMultipleCycles()
    {
        Keyframes.Clear();
        Keyframes.Define("fade2",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade2 1s linear 2");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.25f);
        Assert.Equal(0.25f, panel.ComputedStyle.Opacity, 3);
        // Second iteration restarts from the from keyframe.
        ui.Update(1f);
        Assert.Equal(0.25f, panel.ComputedStyle.Opacity, 3);
        // Done after two full iterations.
        ui.Update(0.75f);
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void AnimationFractionalIterationCountStopsMidCycle()
    {
        Keyframes.Clear();
        Keyframes.Define("fade15",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade15 1s linear 1.5 forwards");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(1.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3); // half of the second iteration
    }

    [Fact]
    public void AnimationReverseDirectionPlaysBackwards()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-r",
            KeyframeFrame.At(0f, ("opacity", "0.2")),
            KeyframeFrame.At(1f, ("opacity", "0.8")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-r 1s linear reverse");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0.8f, panel.ComputedStyle.Opacity, 3); // starts from the "to" keyframe
        ui.Update(0.25f);
        Assert.Equal(0.65f, panel.ComputedStyle.Opacity, 3); // 1 - 0.25 through the curve
    }

    [Fact]
    public void AnimationAlternateDirectionFlipsEveryIteration()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-a",
            KeyframeFrame.At(0f, ("opacity", "0.2")),
            KeyframeFrame.At(1f, ("opacity", "0.8")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-a 1s linear infinite alternate");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.25f);
        Assert.Equal(0.35f, panel.ComputedStyle.Opacity, 3); // first iteration: forward
        ui.Update(1f); // elapsed 1.25 → 0.25 into the second (reversed) iteration
        Assert.Equal(0.65f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void AnimationDelayDefersStartWithBackwardsFill()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-d",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-d 1s linear 0.5s backwards");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3); // backwards fill during the delay
        ui.Update(0.25f);
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3); // still in the delay
        ui.Update(0.5f); // elapsed 0.75 → 0.25 into the animation
        Assert.Equal(0.25f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void AnimationNegativeDelayStartsMidway()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-nd",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-nd 1s linear -0.5s");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void PausedAnimationFreezesInPlace()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-p",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-p 1s linear paused");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.5f);
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3); // never advanced
    }

    [Fact]
    public void TogglingPlayStateDoesNotRestartTheAnimation()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-ps",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-ps 1s linear");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);

        panel.SetInlineStyle("animation", "fade-ps 1s linear paused");
        ui.Render();
        ui.Update(0.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3); // frozen

        panel.SetInlineStyle("animation", "fade-ps 1s linear");
        ui.Render();
        ui.Update(0.5f);
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3); // resumes, does not restart
    }

    [Fact]
    public void InfiniteAnimationNeverReverts()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-inf",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "fade-inf 1s linear infinite");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(2.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);
        ui.Update(0.25f); // elapsed 2.75 → 0.75 into the third iteration
        Assert.Equal(0.75f, panel.ComputedStyle.Opacity, 3); // still running
    }

    [Fact]
    public void AnimationEasingIsAppliedToKeyframes()
    {
        Keyframes.Clear();
        Keyframes.Define("fade-e",
            KeyframeFrame.At(0f, ("opacity", "0")),
            KeyframeFrame.At(1f, ("opacity", "1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        // ease-in is below linear at the midpoint (≈0.31 instead of 0.5).
        panel.SetInlineStyle("animation", "fade-e 1s ease-in");
        ui.Screen.AddChild(panel);

        ui.Render();
        ui.Update(0.5f);
        Assert.InRange(panel.ComputedStyle.Opacity, 0.2f, 0.4f);
    }

    [Fact]
    public void AnimationAnimatesTransformComponents()
    {
        Keyframes.Clear();
        Keyframes.Define("slide",
            KeyframeFrame.At(0f, ("translate-x", "0px"), ("rotate", "0deg")),
            KeyframeFrame.At(1f, ("translate-x", "100px"), ("rotate", "360deg")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "slide 1s linear");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0f, panel.ComputedStyle.TranslateX, 3);
        ui.Update(0.5f);
        Assert.Equal(50f, panel.ComputedStyle.TranslateX, 3);
        Assert.Equal(180f, panel.ComputedStyle.Rotate, 3);
    }

    [Fact]
    public void StylesheetKeyframesDriveThePanelEndToEnd()
    {
        Keyframes.Clear();
        using var ui = TestUi.Create();
        ui.LoadStyles("@keyframes fade { from { opacity: 0; } to { opacity: 1; } } .box { animation: fade 1s linear; }");
        var panel = new Panel();
        panel.AddClass("box");
        ui.Screen.AddChild(panel);

        ui.Render();
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3);
        ui.Update(0.5f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);
    }

    // ---- transition integration --------------------------------------------

    [Fact]
    public void TransitionInterpolatesOnlyTheListedProperties()
    {
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("transition", "background-color 0.2s linear");
        panel.SetInlineStyle("background-color", "#ff0000");
        panel.SetInlineStyle("opacity", "1");
        ui.Screen.AddChild(panel);
        ui.Render();

        panel.SetInlineStyle("background-color", "#0000ff");
        panel.SetInlineStyle("opacity", "0");
        ui.Render();
        ui.Update(0.1f);

        // background-color is listed: interpolated to the midpoint.
        Assert.Equal(new UiColor(128, 0, 128, 255), panel.ComputedStyle.BackgroundColor);
        // opacity is not listed: it snaps to the new value.
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void TransitionDelayWaitsBeforeInterpolating()
    {
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("transition", "opacity 0.2s linear 0.1s");
        panel.SetInlineStyle("opacity", "1");
        ui.Screen.AddChild(panel);
        ui.Render();

        panel.SetInlineStyle("opacity", "0");
        ui.Render();
        ui.Update(0.05f);
        Assert.Equal(1f, panel.ComputedStyle.Opacity, 3); // still within the delay

        ui.Update(0.1f); // elapsed 0.15 → 0.25 into the 0.2s duration
        Assert.Equal(0.75f, panel.ComputedStyle.Opacity, 3);

        ui.Update(0.2f);
        Assert.Equal(0f, panel.ComputedStyle.Opacity, 3);
    }

    [Fact]
    public void TransitionAnimatesTransformComponents()
    {
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("transition", "transform 0.2s linear");
        panel.SetInlineStyle("transform", "translate(0px, 0px)");
        ui.Screen.AddChild(panel);
        ui.Render();

        panel.SetInlineStyle("transform", "translate(100px, 0px)");
        ui.Render();
        ui.Update(0.1f);
        Assert.Equal(50f, panel.ComputedStyle.TranslateX, 3);
    }

    [Fact]
    public void TransitionAllInterpolatesEveryAnimatableProperty()
    {
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("transition", "all 0.2s linear");
        panel.SetInlineStyle("opacity", "1");
        panel.SetInlineStyle("background-color", "#ff0000");
        ui.Screen.AddChild(panel);
        ui.Render();

        panel.SetInlineStyle("opacity", "0");
        panel.SetInlineStyle("background-color", "#0000ff");
        ui.Render();
        ui.Update(0.1f);
        Assert.Equal(0.5f, panel.ComputedStyle.Opacity, 3);
        Assert.Equal(new UiColor(128, 0, 128, 255), panel.ComputedStyle.BackgroundColor);
    }

    [Fact]
    public void TransitionRunsAlongsideAnimationOnDifferentProperties()
    {
        Keyframes.Clear();
        Keyframes.Define("pulse",
            KeyframeFrame.At(0f, ("scale-x", "1")),
            KeyframeFrame.At(1f, ("scale-x", "1.1")));
        using var ui = TestUi.Create();
        var panel = new Panel();
        panel.SetInlineStyle("animation", "pulse 1s linear infinite alternate");
        panel.SetInlineStyle("transition", "background-color 0.2s linear");
        panel.SetInlineStyle("background-color", "#ff0000");
        ui.Screen.AddChild(panel);
        ui.Render();

        // The animation drives scale-x; the transition drives background-color.
        panel.SetInlineStyle("background-color", "#0000ff");
        ui.Render();
        ui.Update(0.1f);
        Assert.Equal(1.01f, panel.ComputedStyle.ScaleX, 3); // animation advanced on the same tick
        Assert.Equal(new UiColor(128, 0, 128, 255), panel.ComputedStyle.BackgroundColor);

        // Both keep advancing on the same tick.
        ui.Update(0.4f); // animation elapsed 0.5 → scale 1.05; transition done
        Assert.Equal(1.05f, panel.ComputedStyle.ScaleX, 3);
        Assert.Equal(new UiColor(0, 0, 255, 255), panel.ComputedStyle.BackgroundColor);
    }

    // ---- timing functions ---------------------------------------------------

    [Fact]
    public void TimingFunctionsEvaluateKeywords()
    {
        Assert.Equal(0.5f, TimingFunctions.Evaluate("linear", 0.5f), 3);
        Assert.Equal(0f, TimingFunctions.Evaluate("linear", 0f), 3);
        Assert.Equal(1f, TimingFunctions.Evaluate("linear", 1f), 3);
        // The CSS `ease` curve (cubic-bezier(0.25, 0.1, 0.25, 1)) is past the
        // midpoint at input 0.5; ease-in lags behind, ease-out runs ahead.
        Assert.InRange(TimingFunctions.Evaluate("ease", 0.5f), 0.79f, 0.82f);
        Assert.InRange(TimingFunctions.Evaluate("ease-in", 0.5f), 0.2f, 0.4f);
        Assert.InRange(TimingFunctions.Evaluate("ease-out", 0.5f), 0.6f, 0.8f);
        Assert.Equal(0.5f, TimingFunctions.Evaluate("ease-in-out", 0.5f), 3);
    }

    [Fact]
    public void TimingFunctionsEvaluateCubicBezierAndSteps()
    {
        Assert.Equal(0.5f, TimingFunctions.Evaluate("cubic-bezier(0, 0, 1, 1)", 0.5f), 3);
        Assert.Equal(0f, TimingFunctions.Evaluate("steps(4, end)", 0.24f), 3);
        Assert.Equal(0.25f, TimingFunctions.Evaluate("steps(4, end)", 0.26f), 3);
        Assert.Equal(0.25f, TimingFunctions.Evaluate("steps(4, start)", 0.05f), 3);
        Assert.Equal(1f, TimingFunctions.Evaluate("step-start", 0.5f), 3);
        Assert.Equal(1f, TimingFunctions.Evaluate("step-start", 0f), 3);
        Assert.Equal(0f, TimingFunctions.Evaluate("step-end", 0.5f), 3);
        Assert.Equal(0f, TimingFunctions.Evaluate("step-end", 0f), 3);
        Assert.Equal(1f, TimingFunctions.Evaluate("step-end", 1f), 3);
    }
}
