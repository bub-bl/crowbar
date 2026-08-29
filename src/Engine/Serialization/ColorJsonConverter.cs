using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowbar.Engine.Serialization;

public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.R);
        writer.WriteNumberValue(value.G);
        writer.WriteNumberValue(value.B);
        writer.WriteNumberValue(value.A);
        writer.WriteEndArray();
    }

    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var channels = Vector2JsonConverter.ReadArray(ref reader, 4, "Color");
        return new Color(channels[0], channels[1], channels[2], channels[3]);
    }
}
