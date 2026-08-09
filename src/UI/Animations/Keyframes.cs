namespace Crowbar.UI;

/// <summary>
/// A single keyframe of an animation: a progress offset in [0, 1] and the CSS
/// declarations applied (and interpolated) at that point.
/// </summary>
/// <param name="Offset">Progress in [0, 1] (<c>from</c> = 0, <c>to</c> = 1).</param>
/// <param name="Declarations">CSS property declarations applied at this keyframe.</param>
public sealed record KeyframeFrame(float Offset, IReadOnlyDictionary<string, string> Declarations)
{
    /// <summary>Builds a keyframe from property/value pairs (case-insensitive keys).</summary>
    public static KeyframeFrame At(float offset, params (string Property, string Value)[] declarations) =>
        new(offset, declarations.ToDictionary(d => d.Property, d => d.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A named <c>@keyframes</c> definition: an ordered set of keyframes.</summary>
public sealed record KeyframeList(string Name, IReadOnlyList<KeyframeFrame> Frames);

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
        Registry[keyframes.Name] = keyframes with { Frames = sorted };
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
    /// computed style. Animatable properties interpolate between the keyframes
    /// that surround the progress point; properties declared by a single
    /// keyframe hold their value through the segment (CSS hold semantics);
    /// non-animatable properties snap at their keyframe boundary.
    /// </summary>
    public static ComputedStyle Sample(ComputedStyle baseStyle, KeyframeList keyframes, float progress)
    {
        var result = baseStyle.Clone();
        var frames = keyframes.Frames;
        if (frames.Count == 0) return result;
        progress = Math.Clamp(progress, 0f, 1f);

        var idx = 0;
        while (idx < frames.Count - 1 && frames[idx + 1].Offset <= progress) idx++;
        var lo = frames[idx];
        var hi = idx + 1 < frames.Count ? frames[idx + 1] : null;
        var span = hi is null ? 0f : hi.Offset - lo.Offset;

        if (hi is null || span <= 1e-6f)
        {
            // A single keyframe (or progress exactly at the last one): apply it.
            ApplyFrame(result, lo);
            return result;
        }

        // The declared names of a frame, with the `transform` shorthand expanded
        // into its animatable components so shorthand-driven keyframes
        // interpolate like the longhands.
        var loNames = EffectiveNames(lo);
        var hiNames = EffectiveNames(hi);

        // Values held from keyframes before this segment: the last keyframe
        // that specified a property keeps providing its value until the next
        // keyframe that specifies it (CSS interpolation rules).
        var held = new Dictionary<string, (string ApplyTo, string Value)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < idx; i++)
        {
            foreach (var (name, value) in frames[i].Declarations)
            {
                if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var component in TransformComponents) held[component] = ("transform", value);
                }
                else if (!IsAnimationProperty(name))
                {
                    held[name] = (name, value);
                }
            }
        }

        var localT = Math.Clamp((progress - lo.Offset) / span, 0f, 1f);
        var fromStyle = baseStyle.Clone();
        ApplyFrame(fromStyle, lo);
        var toStyle = baseStyle.Clone();
        ApplyFrame(toStyle, hi);

        foreach (var property in CssProperties.All)
        {
            var name = property.Name;
            var loDeclares = loNames.Contains(name);
            var hiDeclares = hiNames.Contains(name);

            if (loDeclares && hiDeclares)
            {
                if (property.Animatable)
                    property.SetValue(result,
                        property.Lerp(property.GetValue(fromStyle), property.GetValue(toStyle), localT) ?? property.GetValue(toStyle));
                else if (localT >= 1f) property.SetValue(result, property.GetValue(toStyle));
                else property.SetValue(result, property.GetValue(fromStyle));
            }
            else if (loDeclares)
            {
                // Declared by the segment's start: held through the segment.
                property.SetValue(result, property.GetValue(fromStyle));
            }
            else if (hiDeclares)
            {
                // Declared only by the segment's end: the value held from before
                // the segment stays until the boundary, where it snaps.
                if (held.TryGetValue(name, out var heldEntry)) CssProperties.TryApply(result, heldEntry.ApplyTo, heldEntry.Value);
                if (localT >= 1f) property.SetValue(result, property.GetValue(toStyle));
            }
            else if (held.TryGetValue(name, out var heldEntry))
            {
                // Not declared by this segment: keep the last specified value.
                CssProperties.TryApply(result, heldEntry.ApplyTo, heldEntry.Value);
            }
        }
        return result;
    }

    private static readonly string[] TransformComponents = ["translate-x", "translate-y", "scale-x", "scale-y", "rotate"];

    private static HashSet<string> EffectiveNames(KeyframeFrame frame)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in frame.Declarations.Keys)
        {
            if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var component in TransformComponents) names.Add(component);
            }
            else if (!IsAnimationProperty(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    private static void ApplyFrame(ComputedStyle style, KeyframeFrame frame)
    {
        foreach (var (name, value) in frame.Declarations)
        {
            if (IsAnimationProperty(name)) continue;
            CssProperties.TryApply(style, name, value);
        }
    }

    /// <summary>Keyframes cannot animate the animation/transition descriptors themselves.</summary>
    private static bool IsAnimationProperty(string name) =>
        name.StartsWith("animation", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("transition", StringComparison.OrdinalIgnoreCase);
}
