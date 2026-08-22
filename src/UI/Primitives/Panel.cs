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
    // Cached read-only view: the previous `new ReadOnlyCollection` per access
    // was the top GC allocation type in the profiler (every cascade and paint
    // pass touches Children for every panel). The wrapper observes the backing
    // list by reference, so it stays valid as children are added/removed.
    private IReadOnlyList<Panel>? _childrenView;
    private readonly HashSet<string> _classes = new(StringComparer.OrdinalIgnoreCase);

    // Styling state: _resting is the last applied resting (non-animated)
    // computed style, used for change detection. The visible ComputedStyle is
    // composed each frame from _resting, the running keyframe animations
    // (later entries overlay earlier ones) and the active per-property
    // transitions. _computeBuffer is the reusable scratch the cascade fills on
    // every pass; ApplyComputedStyle adopts it as the visible style (equal
    // path) or clones it (first/change path), so no per-pass allocation is
    // needed for style-stable panels.
    private ComputedStyle? _resting;
    private ComputedStyle _computeBuffer = new();
    private readonly List<PanelAnimation> _animations = [];
    private readonly Dictionary<string, PropertyTransition> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _animationDrivenProps;
    private bool _hasComputedStyle;
    private bool _isEnabled = true;
    private bool _isChecked;
    internal bool LayoutDirty { get; private set; } = true;
    /// <summary>Set when only the panel's paint changed (hover, caret, scroll, animation tick).</summary>
    internal bool PaintDirty { get; private set; }
    /// <summary>Set when the cascade may produce a different style (classes, inline style, pseudo-state).</summary>
    internal bool StyleDirty { get; private set; }
    internal bool StyleMayAffectLayout { get; private set; }

    private readonly HashSet<string> _scopeIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ScopeIds => _scopeIds;
    /// <summary>
    /// The reconciliation identity of this panel in the render tree (the
    /// positional path the Razor parser assigns, e.g. "root/2/0"). Stable
    /// across re-renders as long as the structure is unchanged, unlike the
    /// panel instance itself (the tree is rebuilt on every render) — hit-test
    /// logic such as double-click detection compares keys, not references.
    /// </summary>
    public string? Key { get; internal set; }
    public Panel? Parent { get; private set; }
    public IReadOnlyList<Panel> Children => _childrenView ??= new ReadOnlyCollection<Panel>(_children);
    /// <summary>
    /// The backing list of <see cref="Children"/>. Per-frame tree walks
    /// (cascade, layout, paint) iterate the concrete <see cref="List{T}"/> so
    /// the struct enumerator is never boxed — a <c>foreach</c> over the
    /// <c>IReadOnlyList&lt;Panel&gt;</c> interface allocates an <c>IEnumerator</c>
    /// on every walk, the second-largest allocation type in the profiler.
    /// </summary>
    internal List<Panel> ChildrenInternal => _children;
    internal HashSet<string> ClassesInternal => _classes;
    /// <summary>Version of selector-visible state, incremented on this panel and its ancestors when it changes.</summary>
    internal int SelectorVersion { get; private set; }
    /// <summary>
    /// Incremented when the panel's cascaded resting style changed. The layout
    /// engine uses it to mark the panel's Yoga node dirty on the next pass
    /// (Yoga.Net style setters do not dirty nodes themselves).
    /// </summary>
    internal int CascadeVersion { get; private set; }
    /// <summary>True once the cascade has run at least once for this panel.</summary>
    internal bool HasComputedStyle => _hasComputedStyle;
    private string _tagName = "div";
    public string TagName
    {
        get => _tagName;
        set
        {
            if (string.Equals(_tagName, value, StringComparison.Ordinal)) return;
            _tagName = value;
            MarkStyleDirty();
        }
    }
    private string? _id;
    public string? Id
    {
        get => _id;
        set
        {
            if (string.Equals(_id, value, StringComparison.Ordinal)) return;
            _id = value;
            MarkStyleDirty();
        }
    }
    public IReadOnlySet<string> Classes => _classes;
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> InlineStyle { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Text { get; set; } = string.Empty;
    /// <summary>Tooltip text shown near the cursor while the panel is hovered (the <c>tooltip</c> attribute).</summary>
    public string? Tooltip { get; set; }

    public void AddScope(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId)) return;
        if (_scopeIds.Add(scopeId))
        {
            Attributes[scopeId] = string.Empty;
            MarkStyleDirty();
        }
    }
    public bool HasScope(string scopeId) => !string.IsNullOrEmpty(scopeId) && _scopeIds.Contains(scopeId);

    public ComputedStyle ComputedStyle { get; internal set; } = new();
    public UiRect Layout { get; internal set; }

    /// <summary>
    /// Called by the layout pass when this panel's resolved rect changed (e.g.
    /// after a window resize). Components may override it to republish geometry-
    /// derived state — the docked scene viewport — without a Razor rebuild.
    /// </summary>
    protected internal virtual void OnLayoutChanged() { }
    /// <summary>
    /// Allows a component whose published contract depends on its first resolved
    /// geometry (for example the native window chrome) to receive the initial
    /// 0 → actual layout notification. Most components intentionally keep the
    /// default false because their first render already schedules their normal
    /// build lifecycle.
    /// </summary>
    protected internal virtual bool NotifyInitialLayoutChanged => false;
    /// <summary>Horizontal scroll offset of the content box, in layout units.</summary>
    public float ScrollX { get; private set; }
    /// <summary>Vertical scroll offset of the content box, in layout units.</summary>
    public float ScrollY { get; private set; }
    /// <summary>Maximum horizontal scroll offset, set by the layout pass from the overflowing children.</summary>
    public float MaxScrollX { get; internal set; }
    /// <summary>Maximum vertical scroll offset, set by the layout pass from the overflowing children.</summary>
    public float MaxScrollY { get; internal set; }
    /// <summary>Generated <c>::before</c> content (text + computed style), set by the cascade.</summary>
    public PseudoContent? PseudoBefore { get; internal set; }
    /// <summary>Generated <c>::after</c> content (text + computed style), set by the cascade.</summary>
    public PseudoContent? PseudoAfter { get; internal set; }
    /// <summary>Text and computed style of a generated <c>::before</c>/<c>::after</c> element.</summary>
    public readonly record struct PseudoContent(string Text, ComputedStyle Style);

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
        // Scrolling only shifts the painted content; the layout boxes are
        // unchanged, so this is a paint-only invalidation.
        InvalidatePaint();
        Scrolled?.Invoke(this);
    }

    /// <summary>Scrolls by the given delta, clamped to the scrollable range.</summary>
    public void ScrollBy(float dx, float dy) => ScrollTo(ScrollX + dx, ScrollY + dy);

    public bool IsVisible { get; set; } = true;
    public bool IsEnabled { get => _isEnabled; set { if (_isEnabled != value) { _isEnabled = value; MarkStyleDirty(); } } }
    public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; MarkStyleDirty(); } } }
    public bool IsHovered { get; private set; }
    public bool IsPressed { get; private set; }
    public bool IsFocused { get; private set; }
    public event Action<Panel>? PointerEnter;
    public event Action<Panel>? PointerExit;
    public event Action<Panel, UiPointerEvent>? PointerMove;
    public event Action<Panel, UiPointerEvent>? PointerDown;
    public event Action<Panel, UiPointerEvent>? PointerUp;
    public event Action<Panel, UiPointerEvent>? DoubleClicked;
    public event Action<Panel, WheelEvent>? PointerWheel;
    public event Action<Panel>? Focused;
    public event Action<Panel>? Blurred;
    public event Action<Panel>? Scrolled;
    public event Action<Panel, KeyEvent>? KeyDown;
    public event Action<Panel, KeyEvent>? KeyUp;
    /// <summary>Raised when the panel is clicked (any panel with a handler, not just buttons).</summary>
    public event Action<UiPointerEvent>? Clicked;

    // Class, inline-style and pseudo-state mutations feed the CSS cascade and
    // are therefore style-dirty: the renderer re-runs the (cheap) cascade pass
    // and only escalates to a full layout when a layout-affecting property
    // actually changed. Structural changes (children, scope attributes) still
    // invalidate the layout directly.
    public void AddClass(string value) { if (_classes.Add(value)) MarkStyleDirty(); }
    public void RemoveClass(string value) { if (_classes.Remove(value)) MarkStyleDirty(); }
    public void AddChild(Panel child)
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Add(child);
        Invalidate();
        TrackNewSubtree(child);
    }
    internal void ReplaceChild(Panel previous, Panel replacement)
    {
        var index = _children.IndexOf(previous);
        if (index < 0) return;
        previous.Parent = null;
        replacement.Parent?._children.Remove(replacement);
        replacement.Parent = this;
        _children[index] = replacement;
        Invalidate();
        TrackNewSubtree(replacement);
    }
    /// <summary>
    /// A subtree built by a Razor rebuild is attached with its panels already
    /// style-dirty: their <see cref="MarkStyleDirty"/> ran before they had a
    /// parent, so the invalidation never reached the Screen and the scoped
    /// cascade (ApplyStylesTracked) would skip them — and the layout walk's
    /// skipClean fast path stops at their clean ancestors, so the fresh panels
    /// would never be cascaded at all. Route the subtree through the scoped
    /// cascade by marking the attached root dirty when any panel in it still
    /// needs one. Subtree walks here are rare (rebuilds, SetContent); the
    /// common case — attaching an already-cascaded subtree — is a no-op.
    /// </summary>
    private void TrackNewSubtree(Panel child)
    {
        if (SubtreeNeedsCascade(child)) child.MarkStyleDirty();
    }
    private static bool SubtreeNeedsCascade(Panel panel)
    {
        if (!panel.HasComputedStyle || panel.StyleDirty) return true;
        foreach (var child in panel.ChildrenInternal)
            if (SubtreeNeedsCascade(child)) return true;
        return false;
    }
    public void RemoveChild(Panel child) { if (_children.Remove(child)) { child.Parent = null; Invalidate(); } }
    public void ClearChildren() { foreach (var child in _children) child.Parent = null; _children.Clear(); Invalidate(); }
    public void SetInlineStyle(string key, string value) { InlineStyle[key] = value; MarkStyleDirty(); }
    /// <summary>Marks the whole subtree as needing a full layout pass (and therefore a full repaint).</summary>
    public void Invalidate()
    {
        LayoutDirty = true;
        for (var current = this; current is not null; current = current.Parent) current.SelectorVersion++;
        Parent?.Invalidate();
    }
    /// <summary>
    /// Marks only the panel's painted output as stale: the next render repaints
    /// the affected region without re-running the Yoga layout. Used by caret
    /// blinking, selection, scrolling, hover/pressed/focus state and animation
    /// ticks that do not move boxes.
    /// </summary>
    public void InvalidatePaint()
    {
        PaintDirty = true;
        for (var p = Parent; p is not null; p = p.Parent)
            if (p is ScreenPanel screen) screen.AnyPaintDirty = true;
    }

    /// <summary>Marks the panel's cascade inputs as changed (classes, inline style, pseudo-state).</summary>
    internal void MarkStyleDirty(bool mayAffectLayout = true)
    {
        StyleDirty = true;
        StyleMayAffectLayout |= mayAffectLayout;
        for (var current = this; current is not null; current = current.Parent) current.SelectorVersion++;
        PaintDirty = true;
        for (var p = Parent; p is not null; p = p.Parent)
            if (p is ScreenPanel screen)
            {
                screen.AnyPaintDirty = true;
                screen.AnyStyleDirty = true;
                screen.AddStyleDirtyRoot(this);
            }
    }
    /// <summary>
    /// Recomputes the panel's resting style into the reusable compute buffer
    /// and returns it. The buffer is reset to defaults in place (no
    /// allocation), then the sheet's rules and the panel's inline styles are
    /// applied. <see cref="ApplyComputedStyle"/> adopts the returned style.
    /// </summary>
    internal ComputedStyle ComputeStyle(StyleSheet? sheet)
    {
        var target = _computeBuffer;
        target.ResetToDefaults();
        if (sheet is not null) sheet.ComputeInto(this, target);
        else StyleSheet.Apply(target, InlineStyle);
        return target;
    }

    internal void ApplyComputedStyle(ComputedStyle target)
    {
        if (!_hasComputedStyle)
        {
            _hasComputedStyle = true;
            CascadeVersion++;
            _resting = target.Clone();
            UpdateAnimations(target);
            ComputedStyle = Compose();
            return;
        }

        UpdateAnimations(target);

        if (_resting is not null && _resting.StylesEqual(target))
        {
            // ComputedStyle can receive inherited values during the layout
            // pass. Adopt the freshly computed buffer as the visible style so
            // inheritance is applied to a pristine style again (otherwise
            // values such as opacity accumulate); the previously visible
            // style becomes the next compute buffer, keeping the visible
            // style distinct from the buffer at every pass boundary. An
            // active transition or animation keeps the composed style instead.
            var previousVisible = ComputedStyle;
            if (_animations.Count == 0 && _transitions.Count == 0)
            {
                ComputedStyle = target;
                _computeBuffer = previousVisible;
            }
            else
            {
                _computeBuffer = target;
            }

            return;
        }

        var previous = _resting ?? target;
        _resting = target.Clone();
        CascadeVersion++;
        StartTransitions(previous, target);
        ComputedStyle = Compose();
        _computeBuffer = target;
    }

    /// <summary>
    /// Carries the running animation/transition clocks and the last composed
    /// style over from a previous panel instance that occupied the same
    /// position. Razor re-renders rebuild the panel tree (see
    /// <see cref="HtmlPanelParser"/>), so without this hand-off every re-render
    /// would restart CSS animations and transitions from scratch. The resting
    /// style target is kept so the next cascade reconciles the clocks against
    /// the freshly computed style instead of restarting them, and a style
    /// change introduced by the re-render still starts a transition from the
    /// currently visible value.
    /// </summary>
    internal void CarryOverAnimationState(Panel previous)
    {
        if (!previous._hasComputedStyle) return;
        _animations.AddRange(previous._animations);
        previous._animations.Clear();
        foreach (var (name, transition) in previous._transitions) _transitions[name] = transition;
        previous._transitions.Clear();
        _resting = previous._resting;
        _hasComputedStyle = true;
        // The composed style is only worth carrying while something is
        // animating: a plain panel gets its style from the cascade anyway, and
        // the clone keeps this panel's inherited-value application from
        // mutating the (discarded) previous panel's style.
        if (_animations.Count > 0 || _transitions.Count > 0) ComputedStyle = previous.ComputedStyle.Clone();
    }

    /// <summary>
    /// Reconciles the running animation list with <paramref name="target"/>'s
    /// animation specs: entries whose config (excluding play-state) is unchanged
    /// keep their clock and state, changed or new entries restart, dropped
    /// entries disappear. A completed animation with forwards/both fill stays
    /// filled until reconfigured.
    /// </summary>
    private void UpdateAnimations(ComputedStyle target)
    {
        var specs = target.Animations;
        var rebuilt = new List<PanelAnimation>(specs.Length);
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var existing = i < _animations.Count ? _animations[i] : null;
            if (existing is not null && SpecsEqual(existing.Spec, spec))
            {
                existing.Spec = spec; // play-state / fill-mode toggle without restart
                rebuilt.Add(existing);
                continue;
            }
            var animation = existing ?? new PanelAnimation();
            animation.Spec = spec;
            animation.Elapsed = -spec.Delay;
            animation.State = spec.HasAnimation ? AnimationState.Running : AnimationState.None;
            animation.LastProgress = -1f;
            UpdateAnimationProgress(animation);
            rebuilt.Add(animation);
        }
        _animations.Clear();
        _animations.AddRange(rebuilt);

        // Refresh the set of properties driven by active (running or filled)
        // animations, and drop transitions on them (animations take precedence).
        var driven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var animation in _animations)
        {
            if (animation.State == AnimationState.None) continue;
            if (!Keyframes.TryGet(animation.Spec.Name, out var keyframes)) continue;
            foreach (var frame in keyframes.Frames)
            {
                foreach (var name in frame.Declarations.Keys)
                {
                    if (CssProperties.TryGet(name, out _)) driven.Add(name);
                }
            }
        }
        _animationDrivenProps = driven.Count == 0 ? null : driven;
        if (_animationDrivenProps is not null)
        {
            foreach (var name in _transitions.Keys.Where(_animationDrivenProps.Contains).ToList()) _transitions.Remove(name);
        }
    }

    /// <summary>True when two specs drive the same animation (play-state excluded: toggling must not restart).</summary>
    private static bool SpecsEqual(AnimationSpec a, AnimationSpec b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(a.Duration - b.Duration) < 0.0001f &&
        string.Equals(a.TimingFunction, b.TimingFunction, StringComparison.OrdinalIgnoreCase) &&
        a.IterationCount.Equals(b.IterationCount) &&
        string.Equals(a.Direction, b.Direction, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(a.Delay - b.Delay) < 0.0001f &&
        string.Equals(a.FillMode, b.FillMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the progress (and completion state) of an animation entry from
    /// its current elapsed time, without advancing the clock.
    /// </summary>
    private static void UpdateAnimationProgress(PanelAnimation animation)
    {
        var spec = animation.Spec;
        if (!Keyframes.TryGet(spec.Name, out var keyframes) || keyframes.Frames.Count == 0)
        {
            // Unknown or missing definition: no visible effect.
            animation.State = AnimationState.None;
            animation.LastProgress = -1f;
            return;
        }
        if (animation.Elapsed < 0)
        {
            // Delay phase: backwards fill shows the first keyframe, otherwise
            // the resting style stays.
            animation.State = AnimationState.Running;
            animation.LastProgress = IsFillBackwards(spec)
                ? TimingFunctions.Evaluate(spec.TimingFunction, DirectionProgress(spec, 0f, 0f))
                : -1f;
            return;
        }
        var duration = Math.Max(0.0001f, spec.Duration);
        var position = animation.Elapsed / duration;
        if (!float.IsPositiveInfinity(spec.IterationCount) && position >= spec.IterationCount)
        {
            // Finished: the final state is where the last (possibly fractional)
            // iteration stopped. Forwards/both fill keeps it applied.
            var completed = MathF.Floor(spec.IterationCount);
            var finalCycle = spec.IterationCount == completed ? 1f : spec.IterationCount - completed;
            var finalIteration = spec.IterationCount == completed ? completed - 1 : completed;
            animation.LastProgress = TimingFunctions.Evaluate(spec.TimingFunction,
                Math.Clamp(DirectionProgress(spec, finalCycle, finalIteration), 0f, 1f));
            animation.State = IsFillForwards(spec) ? AnimationState.Filled : AnimationState.None;
            if (animation.State == AnimationState.Filled) animation.FillProgress = animation.LastProgress;
            return;
        }
        var iteration = MathF.Floor(position);
        var cycle = position - iteration;
        animation.LastProgress = TimingFunctions.Evaluate(spec.TimingFunction,
            Math.Clamp(DirectionProgress(spec, cycle, iteration), 0f, 1f));
        animation.State = AnimationState.Running;
    }

    internal bool AdvanceStyleAnimation(float deltaTime)
    {
        var advanced = false;
        foreach (var animation in _animations)
        {
            if (animation.State != AnimationState.Running) continue;
            if (animation.Spec.PlayState.Equals("paused", StringComparison.OrdinalIgnoreCase)) continue;
            animation.Elapsed += Math.Max(0, deltaTime);
            UpdateAnimationProgress(animation);
            advanced = true;
        }
        if (_transitions.Count > 0) advanced |= AdvanceTransitions(deltaTime);
        // Re-compose whenever something moved this tick — including the tick
        // that removes the last finished transition, so the style snaps to its
        // target instead of staying at the last interpolated value.
        if (advanced)
        {
            var previous = ComputedStyle;
            var composed = Compose();
            ComputedStyle = composed;
            // Keyframe/transition ticks that only move paint (transform,
            // opacity, color, shadows, ...) repaint the damaged region; ticks
            // that change geometry reflow the layout.
            if (previous.LayoutPropsEqual(composed)) InvalidatePaint();
            else Invalidate();
            // Inherited properties (color, opacity, text metrics, shadows) are
            // baked into the children's computed styles during the cascade;
            // when an animation moves one of them, the descendants hold stale
            // values until the cheap inheritance-only refresh re-applies them.
            if (!previous.InheritedPropsEqual(composed)) MarkInheritanceDirty();
        }
        return advanced;
    }

    /// <summary>
    /// Restores the resting (non-inherited, non-animated) computed style. The
    /// inheritance refresh pass calls this to clear previously baked inherited
    /// values before re-applying them from the parent's current composed style.
    /// The resting target is cloned (never assigned by reference): the refresh
    /// mutates the panel's own style in place, and the clone keeps the resting
    /// target pristine for the next refresh.
    /// </summary>
    internal void RestoreRestingStyle()
    {
        if (_resting is null) return;
        if (_animations.Count == 0 && _transitions.Count == 0) ComputedStyle = _resting.Clone();
    }

    /// <summary>
    /// Marks the tree as needing an inheritance refresh: an ancestor's
    /// animation or transition changed an inherited property, so the
    /// descendants' baked values from the last cascade are stale. Records the
    /// animated panel so the refresh can be scoped to its subtree instead of
    /// re-walking the whole tree every frame (continuously animating leaves
    /// would otherwise force an O(tree) pass per frame).
    /// </summary>
    internal void MarkInheritanceDirty()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            if (p is ScreenPanel screen)
            {
                screen.AnyInheritedDirty = true;
                screen.AddInheritanceDirtyRoot(this);
            }
    }

    private bool AdvanceTransitions(float deltaTime)
    {
        var removed = false;
        foreach (var (name, transition) in _transitions)
        {
            transition.Elapsed += Math.Max(0, deltaTime);
            if (transition.Elapsed >= transition.Duration) removed = _transitions.Remove(name) || removed;
        }
        return removed || _transitions.Count > 0;
    }

    /// <summary>
    /// Composes the visible style: the resting base, then the keyframe animation
    /// samples (later entries overlay earlier ones, filled entries keep their
    /// end state), then the per-property transition values.
    /// </summary>
    private ComputedStyle Compose()
    {
        var result = (_resting ?? ComputedStyle).Clone();
        foreach (var animation in _animations)
        {
            if (animation.State == AnimationState.None || animation.LastProgress < 0) continue;
            if (!Keyframes.TryGet(animation.Spec.Name, out var keyframes) || keyframes.Frames.Count == 0) continue;
            var progress = animation.State == AnimationState.Filled ? animation.FillProgress : animation.LastProgress;
            Keyframes.SampleInto(result, keyframes, progress);
        }
        foreach (var (name, transition) in _transitions)
        {
            if (!CssProperties.TryGet(name, out var property)) continue;
            var t = Math.Clamp(transition.Elapsed / Math.Max(0.0001f, transition.Duration), 0f, 1f);
            var value = property.Lerp(transition.From, transition.To, TimingFunctions.Evaluate(transition.Timing, t));
            if (value is not null) property.SetValue(result, value);
        }
        return result;
    }

    /// <summary>
    /// Starts (or restarts) a per-property transition for every animatable
    /// property whose value changed, using the first matching transition spec.
    /// Properties driven by an active animation are left to the animation.
    /// </summary>
    private void StartTransitions(ComputedStyle previous, ComputedStyle target)
    {
        foreach (var property in CssProperties.All.Where(p => p.Animatable))
        {
            var name = property.Name;
            if (_animationDrivenProps?.Contains(name) == true) continue;
            var to = property.GetValue(target);
            if (property.ValuesEqual(previous, target))
            {
                _transitions.Remove(name);
                continue;
            }
            var spec = FindTransitionSpec(name, target);
            if (spec is null || spec.Duration <= 0)
            {
                _transitions.Remove(name);
                continue;
            }
            _transitions[name] = new PropertyTransition(property, property.GetValue(ComputedStyle), to,
                -spec.Delay, spec.Duration, spec.TimingFunction);
        }
    }

    /// <summary>Finds the first transition spec that covers the property (<c>all</c> or a name list).</summary>
    private static TransitionSpec? FindTransitionSpec(string propertyName, ComputedStyle style)
    {
        foreach (var spec in style.Transitions)
        {
            if (spec.Property.Equals("all", StringComparison.OrdinalIgnoreCase)) return spec;
            if (spec.Property.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            var names = spec.Property.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Any(n => n.Equals(propertyName, StringComparison.OrdinalIgnoreCase))) return spec;
        }
        return null;
    }


    /// <summary>Maps a cycle progress to the actual progress honoring the animation direction.</summary>
    private static float DirectionProgress(AnimationSpec spec, float cycle, float iteration)
    {
        var reversed = spec.Direction.ToLowerInvariant() switch
        {
            "reverse" => true,
            "alternate" => ((int)iteration & 1) == 1,
            "alternate-reverse" => ((int)iteration & 1) == 0,
            _ => false
        };
        return reversed ? 1f - cycle : cycle;
    }

    private static bool IsFillBackwards(AnimationSpec spec) => spec.FillMode is "backwards" or "both";
    private static bool IsFillForwards(AnimationSpec spec) => spec.FillMode is "forwards" or "both";
    internal void SetHovered(bool value)
    {
        if (IsHovered == value) return;
        IsHovered = value;
        if (value) PointerEnter?.Invoke(this); else PointerExit?.Invoke(this);
        // Pseudo-state feeds the cascade; the renderer re-runs it and only
        // reflows when a layout-affecting property actually changed.
        MarkStyleDirty(mayAffectLayout: false);
    }
    internal void SetPressed(bool value) { if (IsPressed != value) { IsPressed = value; MarkStyleDirty(mayAffectLayout: false); } }
    internal void SetFocused(bool value) { if (IsFocused != value) { IsFocused = value; MarkStyleDirty(mayAffectLayout: false); } }
    internal void RaisePointerMove(UiPointerEvent e) => PointerMove?.Invoke(this, e);
    internal void RaisePointerDown(UiPointerEvent e) => PointerDown?.Invoke(this, e);
    internal void RaisePointerUp(UiPointerEvent e) => PointerUp?.Invoke(this, e);
    internal void RaiseDoubleClicked(UiPointerEvent e) => DoubleClicked?.Invoke(this, e);
    internal void RaisePointerWheel(WheelEvent e) => PointerWheel?.Invoke(this, e);
    internal void RaiseKeyDown(KeyEvent e) => KeyDown?.Invoke(this, e);
    internal void RaiseKeyUp(KeyEvent e) => KeyUp?.Invoke(this, e);
    internal void RaiseFocused() => Focused?.Invoke(this);
    internal void RaiseBlurred() => Blurred?.Invoke(this);
    internal void RaiseClicked(UiPointerEvent e) => Clicked?.Invoke(e);
    /// <summary>True when a <see cref="Clicked"/> handler is attached (events cannot be read from outside the declaring class).</summary>
    internal bool HasClickedHandler => Clicked is not null;
    /// <summary>True when this panel owns a pointer-down handler (e.g. a drag source).</summary>
    internal bool HasPointerDownHandler => PointerDown is not null;
    /// <summary>True when this panel can receive pointer-captured movement.</summary>
    internal bool HasPointerMoveHandler => PointerMove is not null;
    /// <summary>True when this panel can receive pointer-captured release.</summary>
    internal bool HasPointerUpHandler => PointerUp is not null;
    internal void ClearDirty()
    {
        LayoutDirty = false;
        PaintDirty = false;
        StyleDirty = false;
        StyleMayAffectLayout = false;
        foreach (var child in _children) child.ClearDirty();
    }
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

    /// <summary>True when any panel in the tree carries a pending paint-only invalidation.</summary>
    internal bool AnyPaintDirty { get; set; }
    /// <summary>True when any panel in the tree carries a pending cascade (style) invalidation.</summary>
    internal bool AnyStyleDirty { get; set; }
    /// <summary>True when an ancestor animation/transition moved an inherited property.</summary>
    internal bool AnyInheritedDirty { get; set; }

    // The panels whose animation/transition moved an inherited property this
    // frame; the inheritance refresh walks only their subtrees. Tiny by design
    // (one entry per animated panel per frame), cleared after each refresh.
    private readonly List<Panel> _inheritanceDirtyRoots = [];

    internal IReadOnlyList<Panel> InheritanceDirtyRoots => _inheritanceDirtyRoots;
    internal void AddInheritanceDirtyRoot(Panel panel)
    {
        if (!_inheritanceDirtyRoots.Contains(panel)) _inheritanceDirtyRoots.Add(panel);
    }
    internal void ClearInheritanceDirtyRoots() => _inheritanceDirtyRoots.Clear();

    // The panels whose cascade inputs changed (classes, inline style,
    // pseudo-state) this frame; the style pass re-cascades only their parent's
    // subtree instead of the whole tree. A panel's state change can alter the
    // match of descendant/child selectors (its descendants) and of
    // adjacent/general-sibling selectors (its siblings and their descendants),
    // so the parent's subtree is exactly the affected set. Tiny by design (one
    // entry per mutated panel per frame), cleared after each refresh.
    private readonly List<Panel> _styleDirtyRoots = [];

    internal IReadOnlyList<Panel> StyleDirtyRoots => _styleDirtyRoots;
    internal void AddStyleDirtyRoot(Panel panel)
    {
        if (!_styleDirtyRoots.Contains(panel)) _styleDirtyRoots.Add(panel);
    }
    internal void ClearStyleDirtyRoots() => _styleDirtyRoots.Clear();

    public void SetViewport(float width, float height)
    {
        // No-op when the size did not change: SetViewport is called on every
        // render pass and must not re-invalidate (which would force a full
        // layout every frame even for paint-only changes).
        if (Math.Abs(ScreenWidth - width) < 0.001f && Math.Abs(ScreenHeight - height) < 0.001f) return;
        ScreenWidth = width; ScreenHeight = height;
        Layout = new UiRect(0, 0, width / Scale, height / Scale);
        Invalidate();
    }
}
