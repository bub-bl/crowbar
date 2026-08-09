using System.Collections.ObjectModel;
using System.Text;

namespace Crowbar.UI;

/// <summary>Lifecycle of the keyframe animation attached to a panel.</summary>
internal enum AnimationState
{
    /// <summary>No animation, or a completed one without forwards fill.</summary>
    None,
    /// <summary>Actively advancing (including delays, pauses and infinite loops).</summary>
    Running,
    /// <summary>Completed with forwards/both fill; the end state stays applied until reconfigured.</summary>
    Filled
}

public class Panel
{
    private readonly List<Panel> _children = [];
    private readonly HashSet<string> _classes = new(StringComparer.OrdinalIgnoreCase);
    private ComputedStyle? _styleTarget;
    private ComputedStyle? _styleFrom;
    private float _styleAnimationTime;
    private bool _styleAnimating;
    /// <summary>Animatable properties driven by the active keyframe animation, excluded from the transition.</summary>
    private HashSet<string>? _transitionExclude;

    // Keyframe animation state: a snapshot of the animation descriptors from the
    // last applied computed style plus the running clock. _animationBase is the
    // resting (non-animated) style the keyframes overlay, refreshed on every
    // layout pass so the animation tracks the latest cascade.
    private ComputedStyle? _animationBase;
    private string _animationName = "none";
    private float _animationDuration;
    private string _animationTimingFunction = "ease";
    private float _animationIterationCount = 1;
    private string _animationDirection = "normal";
    private float _animationDelay;
    private string _animationFillMode = "none";
    private string _animationPlayState = "running";
    private float _animationElapsed;
    private AnimationState _animationState;
    /// <summary>Eased progress sampled when a forwards/both animation completed, kept for the fill state.</summary>
    private float _animationFillProgress;
    /// <summary>Eased progress of the last applied keyframe sample (needed when the animation finishes).</summary>
    private float _lastEasedProgress;
    private bool _hasComputedStyle;
    private bool _isEnabled = true;
    private bool _isChecked;
    internal bool LayoutDirty { get; private set; } = true;

    private readonly HashSet<string> _scopeIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ScopeIds => _scopeIds;
    public Panel? Parent { get; private set; }
    public IReadOnlyList<Panel> Children => new ReadOnlyCollection<Panel>(_children);
    public string TagName { get; set; } = "div";
    public string? Id { get; set; }
    public IReadOnlySet<string> Classes => _classes;
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> InlineStyle { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Text { get; set; } = string.Empty;

    public void AddScope(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId)) return;
        if (_scopeIds.Add(scopeId))
        {
            Attributes[scopeId] = string.Empty;
            Invalidate();
        }
    }
    public bool HasScope(string scopeId) => !string.IsNullOrEmpty(scopeId) && _scopeIds.Contains(scopeId);
    public ComputedStyle ComputedStyle { get; internal set; } = new();
    public UiRect Layout { get; internal set; }
    /// <summary>Horizontal scroll offset of the content box, in layout units.</summary>
    public float ScrollX { get; private set; }
    /// <summary>Vertical scroll offset of the content box, in layout units.</summary>
    public float ScrollY { get; private set; }
    /// <summary>Maximum horizontal scroll offset, set by the layout pass from the overflowing children.</summary>
    public float MaxScrollX { get; internal set; }
    /// <summary>Maximum vertical scroll offset, set by the layout pass from the overflowing children.</summary>
    public float MaxScrollY { get; internal set; }
    /// <summary>Resolved padding of the last layout pass (percentages included).</summary>
    public UiThickness LayoutPadding { get; internal set; }
    /// <summary>Resolved border width of the last layout pass.</summary>
    public UiThickness LayoutBorder { get; internal set; }
    /// <summary>Resolved margin of the last layout pass.</summary>
    public UiThickness LayoutMargin { get; internal set; }
    /// <summary>The computed <c>overflow</c> keyword (visible, hidden, scroll, auto, clip).</summary>
    public string Overflow => ComputedStyle.Overflow;
    /// <summary>Scrollbar thickness in px (<c>auto</c> falls back to <see cref="ScrollBars.Thickness"/>).</summary>
    public float ScrollbarThickness => ComputedStyle.ScrollbarWidth > 0 ? ComputedStyle.ScrollbarWidth : ScrollBars.Thickness;
    /// <summary>True when children are clipped to the padding box (hidden, scroll, auto, clip).</summary>
    public bool ClipsContent => Overflow is "hidden" or "scroll" or "auto" or "clip";
    /// <summary>True when the panel can be scrolled by the user (scroll or auto).</summary>
    public bool IsScrollContainer => Overflow is "scroll" or "auto";
    public bool CanScrollHorizontally => IsScrollContainer && MaxScrollX > 0;
    public bool CanScrollVertically => IsScrollContainer && MaxScrollY > 0;
    /// <summary>Resolved content-box width of the last layout pass.</summary>
    public float ClientWidth => Math.Max(0, Layout.Width - LayoutPadding.Left - LayoutPadding.Right - LayoutBorder.Left - LayoutBorder.Right);
    /// <summary>Resolved content-box height of the last layout pass.</summary>
    public float ClientHeight => Math.Max(0, Layout.Height - LayoutPadding.Top - LayoutPadding.Bottom - LayoutBorder.Top - LayoutBorder.Bottom);

    /// <summary>Sets the scroll offset, clamped to the scrollable range. No-op when unchanged.</summary>
    public void ScrollTo(float x, float y)
    {
        var nextX = Math.Clamp(x, 0, Math.Max(0, MaxScrollX));
        var nextY = Math.Clamp(y, 0, Math.Max(0, MaxScrollY));
        if (Math.Abs(nextX - ScrollX) < 0.001f && Math.Abs(nextY - ScrollY) < 0.001f) return;
        ScrollX = nextX;
        ScrollY = nextY;
        Invalidate();
    }

    /// <summary>Scrolls by the given delta, clamped to the scrollable range.</summary>
    public void ScrollBy(float dx, float dy) => ScrollTo(ScrollX + dx, ScrollY + dy);

    public bool IsVisible { get; set; } = true;
    public bool IsEnabled { get => _isEnabled; set { if (_isEnabled != value) { _isEnabled = value; Invalidate(); } } }
    public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; Invalidate(); } } }
    public bool IsHovered { get; private set; }
    public bool IsPressed { get; private set; }
    public bool IsFocused { get; private set; }
    public event Action<Panel>? PointerEnter;
    public event Action<Panel>? PointerExit;
    public event Action<Panel, UiPointerEvent>? PointerDown;
    public event Action<Panel, UiPointerEvent>? PointerUp;

    public void AddClass(string value) { if (_classes.Add(value)) Invalidate(); }
    public void RemoveClass(string value) { if (_classes.Remove(value)) Invalidate(); }
    public void AddChild(Panel child)
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Add(child);
        Invalidate();
    }
    public void RemoveChild(Panel child) { if (_children.Remove(child)) { child.Parent = null; Invalidate(); } }
    public void ClearChildren() { foreach (var child in _children) child.Parent = null; _children.Clear(); Invalidate(); }
    public void SetInlineStyle(string key, string value) { InlineStyle[key] = value; Invalidate(); }
    public void Invalidate() { LayoutDirty = true; Parent?.Invalidate(); }
    internal void ApplyComputedStyle(ComputedStyle target)
    {
        if (!_hasComputedStyle)
        {
            ComputedStyle = target;
            _styleTarget = target.Clone();
            _hasComputedStyle = true;
            UpdateAnimationState(target);
            return;
        }

        UpdateAnimationState(target);

        if (_styleTarget is not null && StylesEqual(_styleTarget, target))
        {
            // ComputedStyle can receive inherited values during the layout
            // pass. Restore the unmodified target before inheritance is
            // applied again, otherwise values such as opacity accumulate. An
            // active transition, a running animation or a filled animation
            // keeps the interpolated style instead.
            if (!_styleAnimating && _animationState == AnimationState.None) ComputedStyle = target;
            return;
        }
        _styleTarget = target.Clone();
        var duration = target.TransitionDuration > 0 ? target.TransitionDuration : ComputedStyle.TransitionDuration;
        var canAnimate = duration > 0 && HasAnimatableTransition(target);
        if (_animationState == AnimationState.Running)
        {
            if (!canAnimate)
            {
                _styleAnimating = false;
                _transitionExclude = null;
                return;
            }
            // The keyframe animation drives its own properties; keep the resting
            // style fresh and transition the properties the animation does not
            // cover (e.g. a hover color while a pulse runs).
            _animationBase = target.Clone();
            _styleFrom = ComputedStyle.Clone();
            _styleAnimationTime = -target.TransitionDelay;
            _transitionExclude = AnimationPropertyNames();
            _styleAnimating = true;
            return;
        }
        if (_animationState == AnimationState.Filled)
        {
            // A filled animation keeps its end state over the new resting
            // values (CSS: fill wins over normal styles until the animation is
            // removed or reconfigured).
            ReapplyFillState(target);
            _styleAnimating = false;
            _transitionExclude = null;
            return;
        }
        if (!canAnimate)
        {
            ComputedStyle = target;
            _styleAnimating = false;
            _transitionExclude = null;
            return;
        }
        _styleFrom = ComputedStyle.Clone();
        _styleAnimationTime = -target.TransitionDelay;
        _transitionExclude = null;
        _styleAnimating = true;
    }

    /// <summary>
    /// Starts, restarts, pauses or stops the keyframe animation declared by
    /// <paramref name="target"/>. Play-state changes (running ↔ paused) never
    /// restart the animation; changing any other descriptor does. A completed
    /// animation with forwards/both fill stays filled until reconfigured.
    /// </summary>
    private void UpdateAnimationState(ComputedStyle target)
    {
        var hasAnimation = !string.IsNullOrWhiteSpace(target.AnimationName) &&
                           !target.AnimationName.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (!hasAnimation)
        {
            _animationState = AnimationState.None;
            return;
        }
        if (_animationState == AnimationState.Filled)
        {
            if (AnimationConfigChanged(target)) StartAnimation(target);
            else _animationBase = target.Clone();
            return;
        }
        if (_animationState != AnimationState.Running || AnimationConfigChanged(target))
        {
            StartAnimation(target);
            return;
        }
        // Same animation, still running: refresh the base style (so keyframes
        // overlay the latest resting values) and honor play-state toggles.
        _animationBase = target.Clone();
        _animationPlayState = target.AnimationPlayState;
        _animationFillMode = target.AnimationFillMode;
    }

    private void StartAnimation(ComputedStyle target)
    {
        _animationName = target.AnimationName;
        _animationDuration = target.AnimationDuration;
        _animationTimingFunction = target.AnimationTimingFunction;
        _animationIterationCount = target.AnimationIterationCount;
        _animationDirection = target.AnimationDirection;
        _animationDelay = target.AnimationDelay;
        _animationFillMode = target.AnimationFillMode;
        _animationPlayState = target.AnimationPlayState;
        _animationBase = target.Clone();
        _animationElapsed = -target.AnimationDelay;
        _animationState = AnimationState.Running;
        _styleAnimating = false;
        _transitionExclude = null;

        // Apply the animation's initial state immediately: the first keyframe,
        // the base style during a positive delay without backwards fill, or the
        // mid position when a negative delay starts the animation partway.
        ApplyInitialAnimationFrame();
    }

    private void ApplyInitialAnimationFrame()
    {
        if (_animationBase is null) return;
        if (!Keyframes.TryGet(_animationName, out var keyframes) || keyframes.Frames.Count == 0)
        {
            ComputedStyle = _animationBase;
            return;
        }
        if (_animationElapsed < 0)
        {
            if (IsFillBackwards()) ApplyAnimationStyle(keyframes, DirectionProgress(0f));
            else ComputedStyle = _animationBase;
            return;
        }
        var duration = Math.Max(0.0001f, _animationDuration);
        var position = _animationElapsed / duration;
        var iteration = MathF.Floor(position);
        ApplyAnimationStyle(keyframes, DirectionProgress(position - iteration, iteration));
    }

    private bool AnimationConfigChanged(ComputedStyle target) =>
        !string.Equals(_animationName, target.AnimationName, StringComparison.OrdinalIgnoreCase) ||
        Math.Abs(_animationDuration - target.AnimationDuration) > 0.0001f ||
        !string.Equals(_animationTimingFunction, target.AnimationTimingFunction, StringComparison.OrdinalIgnoreCase) ||
        Math.Abs(_animationIterationCount - target.AnimationIterationCount) > 0.0001f ||
        !string.Equals(_animationDirection, target.AnimationDirection, StringComparison.OrdinalIgnoreCase) ||
        Math.Abs(_animationDelay - target.AnimationDelay) > 0.0001f;

    internal bool AdvanceStyleAnimation(float deltaTime)
    {
        var advanced = false;
        // The keyframe animation runs first, then the transition overlays the
        // properties the animation does not drive (CSS allows both at once, e.g.
        // a pulse animation plus a hover color transition).
        if (_animationState == AnimationState.Running) advanced = AdvanceKeyframeAnimation(deltaTime);
        if (_styleAnimating && _styleTarget is not null && _styleFrom is not null)
            advanced |= AdvanceTransition(deltaTime);
        return advanced;
    }

    private bool AdvanceTransition(float deltaTime)
    {
        var duration = Math.Max(0.001f, _styleTarget!.TransitionDuration > 0 ? _styleTarget.TransitionDuration : _styleFrom!.TransitionDuration);
        _styleAnimationTime = Math.Min(duration, _styleAnimationTime + Math.Max(0, deltaTime));
        var t = Math.Clamp(_styleAnimationTime / duration, 0f, 1f);
        var eased = TimingFunctions.Evaluate(_styleTarget.TransitionTimingFunction, t);
        ComputedStyle = Interpolate(ComputedStyle, _styleFrom, _styleTarget, eased, _transitionExclude);
        Invalidate();
        if (_styleAnimationTime >= duration)
        {
            // When an animation is running or filled it keeps driving the style;
            // the transition only snaps the non-animated properties.
            if (_animationState == AnimationState.None) ComputedStyle = _styleTarget;
            _styleAnimating = false;
            _transitionExclude = null;
        }
        return true;
    }

    private bool AdvanceKeyframeAnimation(float deltaTime)
    {
        if (_animationBase is null) return false;
        if (_animationPlayState.Equals("paused", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Keyframes.TryGet(_animationName, out var keyframes) || keyframes.Frames.Count == 0)
        {
            // The definition disappeared (e.g. hot reload): back to rest.
            _animationState = AnimationState.None;
            ComputedStyle = _animationBase;
            Invalidate();
            return false;
        }

        _animationElapsed += Math.Max(0, deltaTime);

        // Delay phase: nothing runs, but backwards fill shows the first keyframe.
        if (_animationElapsed < 0)
        {
            if (IsFillBackwards()) ApplyAnimationStyle(keyframes, DirectionProgress(0f));
            else ComputedStyle = _animationBase;
            Invalidate();
            return true;
        }

        var duration = Math.Max(0.0001f, _animationDuration);
        var position = _animationElapsed / duration;
        if (!float.IsPositiveInfinity(_animationIterationCount) && position >= _animationIterationCount)
        {
            // Finished: the final state is where the last (possibly fractional)
            // iteration stopped. Forwards/both fill keeps it, otherwise the
            // element returns to its resting style.
            var completed = MathF.Floor(_animationIterationCount);
            var finalCycle = _animationIterationCount == completed ? 1f : _animationIterationCount - completed;
            var finalIteration = _animationIterationCount == completed ? completed - 1 : completed;
            ApplyAnimationStyle(keyframes, DirectionProgress(finalCycle, finalIteration));
            if (IsFillForwards())
            {
                _animationFillProgress = _lastEasedProgress;
                _animationState = AnimationState.Filled;
            }
            else
            {
                _animationState = AnimationState.None;
                ComputedStyle = _animationBase;
            }
            Invalidate();
            return true;
        }

        var iteration = MathF.Floor(position);
        var cycle = position - iteration;
        ApplyAnimationStyle(keyframes, DirectionProgress(cycle, iteration));
        Invalidate();
        return true;
    }

    private void ApplyAnimationStyle(KeyframeList keyframes, float progress)
    {
        if (_animationBase is null) return;
        var eased = TimingFunctions.Evaluate(_animationTimingFunction, Math.Clamp(progress, 0f, 1f));
        _lastEasedProgress = eased;
        ComputedStyle = Keyframes.Sample(_animationBase, keyframes, eased);
    }

    private void ReapplyFillState(ComputedStyle target)
    {
        _animationBase = target.Clone();
        if (Keyframes.TryGet(_animationName, out var keyframes) && keyframes.Frames.Count > 0)
            ComputedStyle = Keyframes.Sample(_animationBase, keyframes, _animationFillProgress);
        else ComputedStyle = _animationBase;
    }

    /// <summary>Maps a cycle progress to the actual progress honoring the animation direction.</summary>
    private float DirectionProgress(float cycle, float iteration = 0)
    {
        var reversed = _animationDirection.ToLowerInvariant() switch
        {
            "reverse" => true,
            "alternate" => ((int)iteration & 1) == 1,
            "alternate-reverse" => ((int)iteration & 1) == 0,
            _ => false
        };
        return reversed ? 1f - cycle : cycle;
    }

    private bool IsFillBackwards() => _animationFillMode is "backwards" or "both";
    private bool IsFillForwards() => _animationFillMode is "forwards" or "both";

    /// <summary>
    /// True when the transition-property list names at least one registered
    /// animatable property. Data-driven through <see cref="CssProperties"/> so
    /// a newly registered animatable property transitions without extra wiring.
    /// The <c>transform</c> keyword expands to its animatable components.
    /// </summary>
    private static bool HasAnimatableTransition(ComputedStyle style)
    {
        if (style.TransitionProperty.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        var names = style.TransitionProperty.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Any(n => n.Equals("transform", StringComparison.OrdinalIgnoreCase))) return true;
        return CssProperties.All.Any(p => p.Animatable && names.Any(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool StylesEqual(ComputedStyle a, ComputedStyle b)
    {
        foreach (var property in CssProperties.All)
            if (!property.ValuesEqual(property.GetValue(a), property.GetValue(b))) return false;
        return true;
    }

    private static ComputedStyle Interpolate(ComputedStyle current, ComputedStyle from, ComputedStyle to, float t,
        HashSet<string>? exclude)
    {
        // Start from the new target so non-transitioned properties snap to
        // their new values, then overlay the interpolated properties.
        var result = to.Clone();
        var included = TransitionProperties(to.TransitionProperty);
        foreach (var property in CssProperties.All.Where(p => p.Animatable))
        {
            if (exclude is not null && exclude.Contains(property.Name))
            {
                // Properties driven by an active keyframe animation keep the
                // animation's sampled value.
                property.SetValue(result, property.GetValue(current));
                continue;
            }
            if (!included(property.Name)) continue;
            var value = property.Lerp(property.GetValue(from), property.GetValue(to), t);
            if (value is not null) property.SetValue(result, value);
        }
        return result;
    }

    /// <summary>
    /// The animatable property names the running keyframe animation drives,
    /// used to keep the transition off those properties (CSS: animations take
    /// precedence over transitions). Expands <c>transform</c> to its components.
    /// </summary>
    private HashSet<string>? AnimationPropertyNames()
    {
        if (!Keyframes.TryGet(_animationName, out var keyframes) || keyframes.Frames.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in keyframes.Frames)
        {
            foreach (var name in frame.Declarations.Keys)
            {
                if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add("translate-x");
                    names.Add("translate-y");
                    names.Add("scale-x");
                    names.Add("scale-y");
                    names.Add("rotate");
                }
                else if (CssProperties.TryGet(name, out var property))
                {
                    names.Add(property.Name);
                }
            }
        }
        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// Resolves the <c>transition-property</c> list into a name filter. Only the
    /// named animatable properties interpolate during a transition; <c>all</c>
    /// covers every animatable property. The <c>transform</c> keyword expands
    /// to its animatable components (translate/scale/rotate).
    /// </summary>
    private static Func<string, bool> TransitionProperties(string transitionProperty)
    {
        if (transitionProperty.Equals("all", StringComparison.OrdinalIgnoreCase)) return _ => true;
        var names = transitionProperty.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("transform"))
        {
            set.Add("translate-x");
            set.Add("translate-y");
            set.Add("scale-x");
            set.Add("scale-y");
            set.Add("rotate");
        }
        return set.Contains;
    }
    internal void SetHovered(bool value)
    {
        if (IsHovered == value) return;
        IsHovered = value;
        if (value) PointerEnter?.Invoke(this); else PointerExit?.Invoke(this);
        Invalidate();
    }
    internal void SetPressed(bool value) { if (IsPressed != value) { IsPressed = value; Invalidate(); } }
    internal void SetFocused(bool value) { if (IsFocused != value) { IsFocused = value; Invalidate(); } }
    internal void RaisePointerDown(UiPointerEvent e) => PointerDown?.Invoke(this, e);
    internal void RaisePointerUp(UiPointerEvent e) => PointerUp?.Invoke(this, e);
    internal void ClearDirty() { LayoutDirty = false; foreach (var child in _children) child.ClearDirty(); }
}

public abstract class PanelComponent : Panel
{
    private int _lastBuildHash;
    private int? _skippedBuildHash;
    private bool _built;
    public bool StateDirty { get; private set; } = true;
    public string? RazorFile { get; internal set; }
    public StyleSheet? StyleSheet { get; private set; }
    internal Action? StateChanged { get; set; }

    protected virtual int BuildHash() => 0;
    protected virtual bool ShouldRender() => true;
    protected virtual void OnTreeFirstBuilt() { }
    protected virtual void OnTreeBuilt() { }
    public void StateHasChanged() { _skippedBuildHash = null; StateDirty = true; Invalidate(); StateChanged?.Invoke(); }
    internal bool NeedsBuild()
    {
        var hash = BuildHash();
        if (!StateDirty && _skippedBuildHash == hash) return false;
        return StateDirty || !_built || _lastBuildHash != hash;
    }
    internal bool CanRender() => ShouldRender();
    internal void MarkRenderSkipped() { _skippedBuildHash = BuildHash(); _lastBuildHash = _skippedBuildHash.Value; StateDirty = false; }
    internal bool MarkBuilt(StyleSheet? styleSheet)
    {
        var firstRender = !_built;
        StyleSheet = styleSheet;
        _lastBuildHash = BuildHash();
        StateDirty = false;
        if (firstRender) { _built = true; OnTreeFirstBuilt(); }
        OnTreeBuilt();
        return firstRender;
    }
}

public sealed class ScreenPanel : Panel
{
    public float Scale { get; set; } = 1;
    public float Opacity { get; set; } = 1;
    public int ZIndex { get; set; }
    public bool AutoScreenScale { get; set; }
    public float ScreenWidth { get; private set; }
    public float ScreenHeight { get; private set; }

    public void SetViewport(float width, float height)
    {
        ScreenWidth = width; ScreenHeight = height;
        Layout = new UiRect(0, 0, width / Scale, height / Scale);
        Invalidate();
    }
}
