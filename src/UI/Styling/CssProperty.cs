namespace Crowbar.UI;

/// <summary>
/// A single CSS property understood by the styling engine. Registering a
/// property in <see cref="CssProperties"/> is the extension point of the whole
/// styling pipeline: once registered, the property is applied by
/// <see cref="StyleSheet"/>, compared for change detection, marked as inherited
/// when needed and animated through transitions when animatable.
/// </summary>
public abstract class CssProperty
{
    protected CssProperty(string name, bool inherited = false, bool animatable = false)
    {
        Name = name;
        Inherited = inherited;
        Animatable = animatable;
    }

    /// <summary>CSS property name, matched case-insensitively (e.g. <c>background-color</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// Stable registration index used as the bit position in a
    /// <see cref="ComputedStyle"/>'s written-property mask (see
    /// <see cref="CssProperties.Register"/>). Assigned once at registration;
    /// the registry is append-only after its static constructor, so the index
    /// never changes.
    /// </summary>
    internal int Index { get; set; } = -1;

    /// <summary>
    /// The bit(s) set in a style's written mask when this property is applied.
    /// Simple properties set their own bit; compounds (<c>margin</c>,
    /// <c>border</c>, ...) expand to the longhand leaves they write so change
    /// detection compares the actual compared fields.
    /// </summary>
    internal virtual System.UInt128 WrittenBits => ComputedStyle.BitOf(this);

    /// <summary>True when the value flows down to descendant panels during the layout pass.</summary>
    public bool Inherited { get; }

    /// <summary>True when the value can be smoothly interpolated between two computed styles.</summary>
    public bool Animatable { get; }

    /// <summary>Parses a raw CSS value and applies it to the style. Returns false when the value is invalid.</summary>
    public abstract bool TryApply(ComputedStyle style, string rawValue);

    /// <summary>Current value of the property for the given style, or null when not individually addressable.</summary>
    public abstract object? GetValue(ComputedStyle style);

    /// <summary>
    /// Compares the property's value on two computed styles without boxing.
    /// Change detection (layout/inherited equality, cascade diffing) runs for
    /// every property on every panel on every pass, so implementations read the
    /// typed value from each style directly instead of routing through
    /// <see cref="GetValue"/> (which boxes the <c>CssLength</c>/<c>UiColor</c>/
    /// <c>float</c> values).
    /// </summary>
    public abstract bool StylesEqual(ComputedStyle a, ComputedStyle b);

    /// <summary>Compares only this property's typed value on two styles.</summary>
    public abstract bool ValuesEqual(ComputedStyle a, ComputedStyle b);

    /// <summary>Sets the property value directly (used for defaults and transition interpolation).</summary>
    public abstract void SetValue(ComputedStyle style, object? value);

    /// <summary>Default (initial) value of the property.</summary>
    public abstract object? DefaultValue { get; }

    /// <summary>Compares two values of this property for equality.</summary>
    public abstract bool ValuesEqual(object? a, object? b);

    /// <summary>Linear interpolation between two values, or null when the property is not animatable.</summary>
    public abstract object? Lerp(object? from, object? to, float t);
}

/// <summary>Signature of a raw CSS value parser.</summary>
public delegate bool TryParseHandler<T>(string value, out T result);

/// <summary>
/// Typed implementation of <see cref="CssProperty"/> backed by getter/setter
/// accessors on <see cref="ComputedStyle"/>.
/// </summary>
public sealed class CssProperty<T> : CssProperty
{
    private readonly Func<ComputedStyle, T> _getter;
    private readonly Action<ComputedStyle, T> _setter;
    private readonly TryParseHandler<T> _parser;
    private readonly Func<T, T, float, object?>? _lerper;
    private readonly T _defaultValue;

    internal CssProperty(string name, Func<ComputedStyle, T> getter, Action<ComputedStyle, T> setter,
        TryParseHandler<T> parser, T defaultValue, bool inherited, bool animatable, Func<T, T, float, object?>? lerper)
        : base(name, inherited, animatable)
    {
        _getter = getter;
        _setter = setter;
        _parser = parser;
        _defaultValue = defaultValue;
        _lerper = lerper;
    }

    public override bool TryApply(ComputedStyle style, string rawValue)
    {
        if (!_parser(rawValue, out var value)) return false;
        _setter(style, value);
        return true;
    }

    public override object? GetValue(ComputedStyle style) => _getter(style);
    public override void SetValue(ComputedStyle style, object? value) => _setter(style, (T)value!);
    public override object? DefaultValue => _defaultValue;
    public override bool ValuesEqual(object? a, object? b) => EqualityComparer<T>.Default.Equals((T)a!, (T)b!);
    public override bool StylesEqual(ComputedStyle a, ComputedStyle b) =>
        EqualityComparer<T>.Default.Equals(_getter(a), _getter(b));
    public override bool ValuesEqual(ComputedStyle a, ComputedStyle b) =>
        EqualityComparer<T>.Default.Equals(_getter(a), _getter(b));
    /// <summary>Interpolates, or null when the property cannot interpolate between these values.</summary>
    public override object? Lerp(object? from, object? to, float t) =>
        _lerper is null ? null : _lerper((T)from!, (T)to!, t);
}

/// <summary>
/// Base for properties whose value expands into several sub-properties
/// (<c>margin</c>, <c>padding</c>, <c>gap</c>, <c>transition</c>...). Such
/// properties are applied with custom logic and are not individually
/// addressable for equality or animation.
/// </summary>
public abstract class CompoundCssProperty : CssProperty
{
    // Longhand leaves the compound writes (e.g. <c>margin</c> writes
    // margin-top/right/bottom/left). Change detection marks these instead of
    // the compound itself: compound values are not individually addressable
    // (StylesEqual always returns true), so comparing the leaves is what
    // actually detects a change. Resolved lazily because the leaves are
    // registered after the compound in the built-in table.
    private readonly string[] _leafNames;
    private System.UInt128? _leafBits;

    protected CompoundCssProperty(string name, params string[] leafNames) : base(name)
    {
        _leafNames = leafNames;
    }

    internal override System.UInt128 WrittenBits
    {
        get
        {
            if (_leafNames.Length == 0) return 0;
            if (_leafBits is null)
            {
                var bits = (System.UInt128)0;
                foreach (var leaf in _leafNames)
                    if (CssProperties.TryGet(leaf, out var property)) bits |= ComputedStyle.BitOf(property);
                _leafBits = bits;
            }
            return _leafBits.Value;
        }
    }

    public override object? GetValue(ComputedStyle style) => null;
    public override void SetValue(ComputedStyle style, object? value)
    {
    }

    public override object? DefaultValue => null;
    public override bool ValuesEqual(object? a, object? b) => true;
    public override bool StylesEqual(ComputedStyle a, ComputedStyle b) => true;
    public override bool ValuesEqual(ComputedStyle a, ComputedStyle b) => true;
    public override object? Lerp(object? from, object? to, float t) => null;
}
