using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowbar.Engine.Serialization;

/// <summary>
/// Serializes a <see cref="Model"/> reference: a content path for file-loaded
/// models (<c>{"path": "..."}</c>) or a procedural marker for the engine's
/// primitives (<c>{"procedural": "cube"|"plane"}</c>). Models that cannot be
/// reproduced on load (the error model) serialize as <c>null</c>.
///
/// Reading resolves the reference; a path whose asset is missing or unreadable
/// resolves to null so the surrounding property is simply left unset rather
/// than failing the whole load.
/// </summary>
public sealed class ModelJsonConverter : JsonConverter<Model>
{
    public override void Write(Utf8JsonWriter writer, Model value, JsonSerializerOptions options)
    {
        if (value.ResourcePath is { Length: > 0 } path)
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteEndObject();
            return;
        }

        if (value.IsProcedural)
        {
            switch (value.Name)
            {
                case "Cube":
                    writer.WriteStartObject();
                    writer.WriteString("procedural", "cube");
                    writer.WriteEndObject();
                    return;
                case "Plane":
                    writer.WriteStartObject();
                    writer.WriteString("procedural", "plane");
                    writer.WriteEndObject();
                    return;
            }
        }

        // The error model or an unknown procedural model: not reproducible.
        writer.WriteNullValue();
    }

    public override Model? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A Model must be serialized as an object.");

        string? path = null;
        string? procedural = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Malformed Model object.");

            switch (reader.GetString())
            {
                case "path":
                    reader.Read();
                    path = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "procedural":
                    reader.Read();
                    procedural = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (path is { Length: > 0 })
        {
            try
            {
                return Model.Load(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or JsonException)
            {
                // Missing or corrupt asset: resolve to null (the property stays unset).
                return null;
            }
        }

        return procedural switch
        {
            "cube" => Model.CreateCube(),
            "plane" => Model.CreatePlane(),
            _ => null
        };
    }
}
