using System.Globalization;
using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Named uniform values for one post-process blit. The renderer packs them into
/// the shader's group-0 uniform buffer by field name (case-insensitive,
/// trailing underscores ignored), so the C# side and the shader side of an
/// effect only need to agree on names — no hand-packed float4.
/// </summary>
public sealed class RenderAttributes
{
    private readonly Dictionary<string, ShaderParameter> _values = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, ShaderParameter> Values => _values;

    /// <summary>
    /// True when a shader field name matches an attribute name: case-insensitive
    /// with trailing underscores ignored on both sides, so the Slang field
    /// <c>operator_</c> (avoiding the keyword) matches both the <c>Operator</c>
    /// property and an explicit <c>operator_</c> attribute.
    /// </summary>
    internal static bool MatchesField(string fieldName, string attributeName) =>
        string.Equals(fieldName.TrimEnd('_'), attributeName.TrimEnd('_'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets a named uniform; returns this bag for chaining.</summary>
    public RenderAttributes Set(string name, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _values[name] = ToParameter(value);
        return this;
    }

    private static ShaderParameter ToParameter(object value) => value switch
    {
        float f => f,
        int i => i,
        uint u => u,
        bool b => b,
        Vector2 v => v,
        Vector3 v => v,
        Vector4 v => v,
        Matrix4x4 m => m,
        // Enums pack as their numeric value (a float uniform is the common case).
        Enum e => Convert.ToSingle(e, CultureInfo.InvariantCulture),
        IConvertible convertible => Convert.ToSingle(convertible, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException(
            $"Unsupported post-process uniform of type '{value.GetType().Name}'.", nameof(value))
    };
}
