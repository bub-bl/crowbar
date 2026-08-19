using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowbar.Engine.Serialization;

/// <summary>
/// Serializes a <see cref="Vector2"/> as a JSON array of two numbers (<c>[1, 2]</c>).
/// Reading validates the shape strictly and throws <see cref="JsonException"/> on
/// anything else, so a malformed vector never silently corrupts a level.
/// </summary>
public sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }

    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var components = ReadArray(ref reader, 2, "Vector2");
        return new Vector2(components[0], components[1]);
    }

    /// <summary>Reads a JSON array of exactly <paramref name="count"/> numbers, sharing the strict shape validation.</summary>
    internal static float[] ReadArray(ref Utf8JsonReader reader, int count, string typeName)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"A {typeName} must be serialized as an array of {count} numbers.");

        var components = new float[count];
        var i = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (i >= count)
                throw new JsonException($"A {typeName} must contain exactly {count} numbers.");
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"A {typeName} component must be a number.");
            components[i++] = reader.GetSingle();
        }

        if (i != count)
            throw new JsonException($"A {typeName} must contain exactly {count} numbers.");
        return components;
    }
}

/// <summary>
/// Serializes a <see cref="Vector3"/> as a JSON array of three numbers (<c>[1, 2, 3]</c>).
/// </summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }

    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var components = Vector2JsonConverter.ReadArray(ref reader, 3, "Vector3");
        return new Vector3(components[0], components[1], components[2]);
    }
}

/// <summary>
/// Serializes a <see cref="Vector4"/> as a JSON array of four numbers (<c>[1, 2, 3, 4]</c>).
/// </summary>
public sealed class Vector4JsonConverter : JsonConverter<Vector4>
{
    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }

    public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var components = Vector2JsonConverter.ReadArray(ref reader, 4, "Vector4");
        return new Vector4(components[0], components[1], components[2], components[3]);
    }
}
