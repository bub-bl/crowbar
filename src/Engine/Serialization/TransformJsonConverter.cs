using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowbar.Engine.Serialization;

/// <summary>
/// Serializes a <see cref="Transform"/> as its canonical string form
/// (<c>px,py,pz,rx,ry,rz,rw,sx,sy,sz</c>, culture-invariant) — the same compact
/// representation the engine already exposes through
/// <see cref="Transform.ToString"/> / <see cref="Transform.Parse"/>. The string
/// form keeps <c>.level</c> files diff-friendly, and this converter is the only
/// place that knows it. <see cref="ToCanonical"/> and <see cref="Parse"/> expose
/// the same round trip to the level serializer for the spatial component
/// "transform" field.
/// </summary>
public sealed class TransformJsonConverter : JsonConverter<Transform>
{
    public override void Write(Utf8JsonWriter writer, Transform value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToCanonical(value));

    public override Transform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A Transform must be serialized as its canonical string form.");
        return Parse(reader.GetString()!);
    }

    /// <summary>The canonical string form of a transform, as stored in .level files.</summary>
    public static string ToCanonical(Transform value) => value.ToString();

    /// <summary>Parses the canonical string form, throwing <see cref="JsonException"/> on malformed input.</summary>
    public static Transform Parse(string text)
    {
        try
        {
            return Transform.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new JsonException($"Invalid Transform string '{text}'.", ex);
        }
    }
}
