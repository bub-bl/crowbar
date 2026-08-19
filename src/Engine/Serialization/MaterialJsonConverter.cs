using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crowbar.Engine.Serialization;

/// <summary>
/// Serializes a <see cref="Material"/> as its full definition — a material is
/// built in code, not loaded from an asset file, so the file must be able to
/// rebuild it: the shader name, the technique, the blend state and the shader
/// parameter values.
///
/// Reading rebuilds the material through <see cref="Material.FromShader"/> and
/// re-applies the parameter values the shader still declares (parameters
/// dropped by a newer shader are skipped — forward compatibility). A material
/// whose shader cannot be loaded resolves to null so the surrounding property
/// is simply left unset.
/// </summary>
public sealed class MaterialJsonConverter : JsonConverter<Material>
{
    public override void Write(Utf8JsonWriter writer, Material value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("shader", NormalizeShaderName(value.Shader.Path));
        writer.WriteString("technique", value.Technique);
        writer.WriteString("blendMode", value.BlendMode.ToString());
        writer.WriteBoolean("doubleSided", value.DoubleSided);

        // The shader parameters are typed by their runtime value (the engine's
        // ShaderParameter union exposes them as object?); each value is written
        // through the shared options so the registered converters apply.
        var wroteValues = false;
        foreach (var (name, parameter) in value.Values)
        {
            var raw = parameter.Value;
            if (raw is null)
                continue;

            var element = JsonSerializer.SerializeToElement(raw, raw.GetType(), options);
            if (element.ValueKind == JsonValueKind.Null)
                continue;

            if (!wroteValues)
            {
                writer.WriteStartObject("values");
                wroteValues = true;
            }
            writer.WritePropertyName(name);
            element.WriteTo(writer);
        }
        if (wroteValues)
            writer.WriteEndObject();

        writer.WriteEndObject();
    }

    public override Material? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A Material must be serialized as an object.");

        try
        {
            string? shader = null;
            var technique = "Main";
            var blendMode = "Opaque";
            var doubleSided = false;
            Dictionary<string, JsonElement>? values = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Malformed Material object.");

                switch (reader.GetString())
                {
                    case "shader":
                        reader.Read();
                        shader = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    case "technique":
                        reader.Read();
                        technique = reader.TokenType == JsonTokenType.Null ? "Main" : reader.GetString() ?? "Main";
                        break;
                    case "blendMode":
                        reader.Read();
                        blendMode = reader.TokenType == JsonTokenType.Null ? "Opaque" : reader.GetString() ?? "Opaque";
                        break;
                    case "doubleSided":
                        reader.Read();
                        doubleSided = reader.TokenType == JsonTokenType.True;
                        break;
                    case "values":
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.StartObject)
                            values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ref reader, options);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(shader))
                return null;

            var material = Material.FromShader(shader, technique);
            if (values is not null)
            {
                foreach (var (name, element) in values)
                {
                    var definition = material.Shader.Parameters.FirstOrDefault(p => p.Name == name);
                    if (definition is null)
                        continue; // the shader dropped this parameter → forward compatibility
                    try
                    {
                        var raw = JsonSerializer.Deserialize(element, definition.Type, options);
                        if (raw is not null)
                            material.Set(name, ToShaderParameter(raw));
                    }
                    catch (JsonException)
                    {
                        // A value the shader can no longer represent: skip it.
                    }
                }
            }

            if (Enum.TryParse<MaterialBlendMode>(blendMode, ignoreCase: true, out var mode))
                material.BlendMode = mode;
            if (doubleSided)
                material.DoubleSided = true;

            return material;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or FileNotFoundException or ArgumentException)
        {
            // Malformed content, or a shader/technique that no longer exists:
            // resolve to null so the property is left unset instead of failing.
            return null;
        }
    }

    /// <summary>
    /// Converts a deserialized CLR value into the engine's ShaderParameter
    /// union. The union cannot be serialized by System.Text.Json itself, so
    /// parameter values are deserialized by their reflected parameter type and
    /// converted here — the only manual conversion the material format needs.
    /// </summary>
    private static ShaderParameter ToShaderParameter(object value) => value switch
    {
        float f => f,
        int i => i,
        uint u => u,
        bool b => b,
        Vector2 v2 => v2,
        Vector3 v3 => v3,
        Vector4 v4 => v4,
        _ => throw new JsonException($"Unsupported shader parameter type '{value.GetType().Name}'.")
    };

    /// <summary>
    /// Collapses a shader path as stored on <see cref="Shader.Path"/>
    /// (<c>Shaders/Surface/StandardPbr.wgsl</c>) back to the name
    /// <see cref="Material.FromShader"/> accepts (<c>Surface/StandardPbr</c>).
    /// </summary>
    private static string NormalizeShaderName(string path)
    {
        var name = path;
        if (name.StartsWith("Shaders/", StringComparison.Ordinal))
            name = name["Shaders/".Length..];
        if (name.EndsWith(".wgsl", StringComparison.OrdinalIgnoreCase))
            name = name[..^".wgsl".Length];
        return name;
    }
}
