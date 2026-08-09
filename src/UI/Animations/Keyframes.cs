namespace Crowbar.UI;

/// <summary>A single keyframe of an animation: a progress offset in [0, 1] and the CSS
/// declarations applied (and interpolated) at that point.</summary>
/// <param name="Offset">Progress in [0, 1] (<c>from</c> = 0, <c>to</c> = 1).</param>
/// <param name="Declarations">CSS property declarations applied at this keyframe.</param>
public sealed record KeyframeFrame(float Offset, IReadOnlyDictionary<string, string> Declarations)
{
    /// <summary>Builds a keyframe from property/value pairs (case-insensitive keys).</summary>
    public static KeyframeFrame At(float offset, params (string Property, string Value)[] declarations) =>
        new(offset, declarations.ToDictionary(d => d.Property, d => d.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// A named <c>@keyframes</c> definition: an ordered set of keyframes. The
/// declarations are parsed into typed values once (at construction), so
/// sampling the animation at a progress point never re-parses CSS or allocates:
/// the demo page's infinite animations are sampled every frame, and that path
/// used to clone several styles, build hash sets and dictionaries, and re-run
/// every property parser on each tick.
/// </summary>
public sealed class KeyframeList
{
    /// <summary>Keyframe name.</summary>
    public string Name { get; }

    /// <summary>Raw keyframe frames, in ascending offset order.</summary>
    public IReadOnlyList<KeyframeFrame> Frames { get; }

    /// <summary>Pre-parsed interpolation data, built once at construction.</summary>
    internal CompiledKeyframes Compiled { get; }

    public KeyframeList(string name, IReadOnlyList<KeyframeFrame> frames)
    {
        Name = name;
        Frames = frames;
        Compiled = CompiledKeyframes.Build(frames);
    }
}

/// <summary>
/// Registry of <c>@keyframes</c> definitions, populated by
/// <see cref="StyleSheet.Parse"/> and by user code. Registering a definition
/// under a name makes it usable by the <c>animation</c>/<c>animation-name</c>
/// CSS properties. This is the extension point for custom animations: define a
/// list of keyframes at runtime with <see cref="Define"/> and animate any
/// animatable CSS property.
/// </summary>
public static class Keyframes
{
    private static readonly Dictionary<string, KeyframeList> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered keyframe definition, in registration order.</summary>
    public static IReadOnlyCollection<KeyframeList> All => Registry.Values;

    public static bool TryGet(string name, out KeyframeList keyframes) => Registry.TryGetValue(name, out keyframes!);

    /// <summary>Registers (or replaces) a keyframe definition. Frames are sorted by offset.</summary>
    public static void Register(KeyframeList keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        var sorted = keyframes.Frames.OrderBy(f => f.Offset).ToArray();
        Registry[keyframes.Name] = new KeyframeList(keyframes.Name, sorted);
    }

    /// <summary>
    /// Programmatic API to define a custom animation at runtime:
    /// <c>Keyframes.Define("pop", KeyframeFrame.At(0, ("scale-x", "1")), ...)</c>.
    /// </summary>
    public static KeyframeList Define(string name, params KeyframeFrame[] frames)
    {
        var keyframes = new KeyframeList(name, frames);
        Register(keyframes);
        return keyframes;
    }

    public static bool Remove(string name) => Registry.Remove(name);
    public static void Clear() => Registry.Clear();

    /// <summary>
    /// Samples the keyframe animation at <paramref name="progress"/> (in [0, 1])
    /// onto a clone of <paramref name="baseStyle"/> — the element's resting
    /// computed style. The returned style is always a fresh clone; the base is
    /// never mutated. Animatable properties interpolate between the keyframes
    /// that surround the progress point; properties declared by a single
    /// keyframe hold their value through the segment (CSS hold semantics);
    /// non-animatable properties snap at their keyframe boundary.
    /// </summary>
    public static ComputedStyle Sample(ComputedStyle baseStyle, KeyframeList keyframes, float progress)
    {
        var result = baseStyle.Clone();
        SampleInto(result, keyframes, progress);
        return result;
    }

    /// <summary>
    /// Applies the keyframe animation at <paramref name="progress"/> onto an
    /// existing style. The style must be a private clone (the caller's own
    /// object, never a shared reference); it is mutated in place so several
    /// animations can be composed onto one clone without cloning between them.
    /// </summary>
    internal static void SampleInto(ComputedStyle style, KeyframeList keyframes, float progress)
    {
        var compiled = keyframes.Compiled;
        if (compiled.Segments.Length == 0)
        {
            compiled.ApplyTerminal(style);
            return;
        }

        progress = Math.Clamp(progress, 0f, 1f);
        var idx = compiled.FindSegment(progress);
        if (idx < 0)
        {
            // At (or past) the last keyframe: apply it directly (CSS semantics).
            compiled.ApplyTerminal(style);
            return;
        }

        var segment = compiled.Segments[idx];
        var span = segment.End - segment.Start;
        if (span <= 1e-6f)
        {
            // Degenerate (zero-width) segment: hold the start frame's values.
            compiled.ApplyTerminal(style);
            return;
        }

        var localT = Math.Clamp((progress - segment.Start) / span, 0f, 1f);
        foreach (var entry in segment.Entries) entry.Apply(style, localT);
    }

    /// <summary>Keyframes cannot animate the animation/transition descriptors themselves.</summary>
    internal static bool IsAnimationProperty(string name) =>
        name.StartsWith("animation", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("transition", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pre-parsed keyframe data. Segments join consecutive frames; each segment
/// carries one entry per property that participates in it (declared by its
/// start frame, its end frame, or held from an earlier frame), with the values
/// already parsed to typed form.
/// </summary>
internal sealed class CompiledKeyframes
{
    public readonly CompiledSegment[] Segments;
    public readonly CompiledTerminal Terminal;

    private CompiledKeyframes(CompiledSegment[] segments, CompiledTerminal terminal)
    {
        Segments = segments;
        Terminal = terminal;
    }

    public static CompiledKeyframes Build(IReadOnlyList<KeyframeFrame> frames)
    {
        if (frames.Count == 0) return new CompiledKeyframes([], CompiledTerminal.Empty);
        var segments = new CompiledSegment[Math.Max(0, frames.Count - 1)];
        for (var i = 0; i < frames.Count - 1; i++) segments[i] = CompiledSegment.Build(frames, i);
        return new CompiledKeyframes(segments, CompiledTerminal.Build(frames[^1]));
    }

    /// <summary>
    /// Index of the segment covering <paramref name="progress"/>, or -1 when
    /// progress sits at (or past) the final keyframe, which applies directly.
    /// </summary>
    public int FindSegment(float progress)
    {
        for (var i = 0; i < Segments.Length; i++)
        {
            if (Segments[i].Start > progress) return i - 1;
            if (i == Segments.Length - 1 && Segments[i].End <= progress) return -1;
        }

        return Segments.Length - 1;
    }

    public void ApplyTerminal(ComputedStyle style) => Terminal.Apply(style);

    internal sealed class CompiledTerminal
    {
        public static readonly CompiledTerminal Empty = new([], []);
        private readonly (CssProperty Property, object? Value)[] _values;
        private readonly (CssProperty Property, string Raw)[] _raw;

        private CompiledTerminal((CssProperty Property, object? Value)[] values, (CssProperty Property, string Raw)[] raw)
        {
            _values = values;
            _raw = raw;
        }

        public static CompiledTerminal Build(KeyframeFrame frame)
        {
            var values = new List<(CssProperty Property, object? Value)>();
            var raw = new List<(CssProperty Property, string Raw)>();
            var scratch = new ComputedStyle();
            foreach (var (name, value) in frame.Declarations)
            {
                if (Keyframes.IsAnimationProperty(name)) continue;
                if (!CssProperties.TryGet(name, out var property) || !property.TryApply(scratch, value)) continue;
                var typed = property.GetValue(scratch);
                if (typed is null) raw.Add((property, value));
                else values.Add((property, typed));
            }

            return new CompiledTerminal(values.ToArray(), raw.ToArray());
        }

        public void Apply(ComputedStyle style)
        {
            foreach (var (property, value) in _values) property.SetValue(style, value);
            foreach (var (property, raw) in _raw) property.TryApply(style, raw);
        }
    }
}

/// <summary>One pair of consecutive keyframes with its interpolated properties.</summary>
internal sealed class CompiledSegment
{
    public readonly float Start;
    public readonly float End;
    public readonly CompiledValue[] Entries;

    private CompiledSegment(float start, float end, CompiledValue[] entries)
    {
        Start = start;
        End = end;
        Entries = entries;
    }

    public static CompiledSegment Build(IReadOnlyList<KeyframeFrame> frames, int index)
    {
        var lo = frames[index];
        var hi = frames[index + 1];
        var scratch = new ComputedStyle();

        // Values held from frames before this segment (last declaration wins).
        var heldRaw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < index; i++)
        {
            foreach (var (name, value) in frames[i].Declarations)
            {
                if (!Keyframes.IsAnimationProperty(name)) heldRaw[name] = value;
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in lo.Declarations.Keys)
        {
            if (!Keyframes.IsAnimationProperty(name)) names.Add(name);
        }

        foreach (var name in hi.Declarations.Keys)
        {
            if (!Keyframes.IsAnimationProperty(name)) names.Add(name);
        }

        foreach (var name in heldRaw.Keys) names.Add(name);

        var entries = new List<CompiledValue>(names.Count);
        foreach (var name in names)
        {
            if (!CssProperties.TryGet(name, out var property)) continue;
            var loDeclared = lo.Declarations.TryGetValue(name, out var loRaw);
            var hiDeclared = hi.Declarations.TryGetValue(name, out var hiRaw);
            var isHeld = heldRaw.TryGetValue(name, out var heldValue);

            object? loValue = null, hiValue = null, heldTyped = null;
            string? heldCompoundRaw = null;
            var loCompound = false;
            var hiCompound = false;

            if (loDeclared) loCompound = !ParseInto(scratch, property, loRaw!, out loValue);
            if (hiDeclared) hiCompound = !ParseInto(scratch, property, hiRaw!, out hiValue);
            if (isHeld && heldValue is not null)
            {
                if (ParseInto(scratch, property, heldValue, out var typed)) heldTyped = typed;
                else heldCompoundRaw = heldValue;
            }

            // Compound properties (margin, padding, ...) cannot be addressed by
            // value: matching the original sampler, they are dropped when
            // declared by a segment boundary (only held ones re-apply).
            if (loDeclared && hiDeclared)
            {
                if (loCompound || hiCompound) continue;
                entries.Add(new CompiledValue(property, loValue, hiValue, null, null, true, true, false, false, property.Animatable));
            }
            else if (loDeclared)
            {
                if (loCompound) continue;
                entries.Add(new CompiledValue(property, loValue, null, null, null, true, false, false, false, false));
            }
            else if (hiDeclared)
            {
                if (hiCompound && heldTyped is null && heldCompoundRaw is null) continue;
                entries.Add(new CompiledValue(property, null, hiCompound ? null : hiValue, heldTyped, heldCompoundRaw, false, true, hiCompound, false, false));
            }
            else if (heldTyped is not null || heldCompoundRaw is not null)
            {
                entries.Add(new CompiledValue(property, null, null, heldTyped, heldCompoundRaw, false, false, false, true, false));
            }
        }

        return new CompiledSegment(lo.Offset, hi.Offset, entries.ToArray());
    }

    /// <summary>Parses a raw value into typed form. Returns false for compound properties (GetValue is null).</summary>
    private static bool ParseInto(ComputedStyle scratch, CssProperty property, string raw, out object? typed)
    {
        typed = null;
        if (!property.TryApply(scratch, raw)) return false;
        typed = property.GetValue(scratch);
        return typed is not null;
    }
}

/// <summary>One property's interpolation data inside a segment.</summary>
internal sealed class CompiledValue
{
    private readonly CssProperty _property;
    private readonly object? _lo;
    private readonly object? _hi;
    private readonly object? _held;
    private readonly string? _heldRaw;
    private readonly bool _loDeclared;
    private readonly bool _hiDeclared;
    private readonly bool _hiCompound;
    private readonly bool _heldOnly;
    private readonly bool _animatable;

    public CompiledValue(CssProperty property, object? lo, object? hi, object? held, string? heldRaw,
        bool loDeclared, bool hiDeclared, bool hiCompound, bool heldOnly, bool animatable)
    {
        _property = property;
        _lo = lo;
        _hi = hi;
        _held = held;
        _heldRaw = heldRaw;
        _loDeclared = loDeclared;
        _hiDeclared = hiDeclared;
        _hiCompound = hiCompound;
        _heldOnly = heldOnly;
        _animatable = animatable;
    }

    public void Apply(ComputedStyle style, float t)
    {
        if (_loDeclared && _hiDeclared)
        {
            if (_animatable)
            {
                var value = _property.Lerp(_lo, _hi, t) ?? _hi;
                _property.SetValue(style, value);
            }
            else
            {
                _property.SetValue(style, t >= 1f ? _hi : _lo);
            }

            return;
        }

        if (_loDeclared)
        {
            _property.SetValue(style, _lo);
            return;
        }

        if (_hiDeclared)
        {
            if (_heldRaw is not null) _property.TryApply(style, _heldRaw);
            else if (_held is not null) _property.SetValue(style, _held);
            if (t >= 1f && !_hiCompound) _property.SetValue(style, _hi);
            return;
        }

        if (_heldOnly)
        {
            if (_heldRaw is not null) _property.TryApply(style, _heldRaw);
            else if (_held is not null) _property.SetValue(style, _held);
        }
    }
}
