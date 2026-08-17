using System.Text.Json;

namespace Crowbar.Engine;

/// <summary>Kind of a resource declared by a shader binding.</summary>
public enum ShaderBindingKind
{
    UniformBuffer,
    ReadOnlyStorageBuffer,
    Texture,
    Sampler
}

/// <summary>
/// One resource binding declared by the shader (group = descriptor set, slot =
/// binding index). <see cref="TypeName"/> is the struct name for a uniform
/// buffer, <c>mat4x4&lt;f32&gt;</c> for a raw matrix uniform, the texture type,
/// or the sampler type.
/// </summary>
public sealed record ShaderBinding(int Group, uint Slot, ShaderBindingKind Kind, string VariableName, string TypeName);

/// <summary>One field of a shader struct: name plus type (e.g. <c>vec4&lt;f32&gt;</c>).</summary>
public sealed record ShaderStructField(string Name, string Type);

/// <summary>A shader struct definition.</summary>
public sealed record ShaderStruct(string Name, IReadOnlyList<ShaderStructField> Fields);

/// <summary>
/// A render pass: a vertex/fragment entry-point pair. <c>vs_main</c>/<c>fs_main</c>
/// becomes "Main", and a material selects one by name.
/// </summary>
public sealed record ShaderTechnique(string Name, string VertexEntryPoint, string FragmentEntryPoint);

public sealed record SlangBindingsAndStructs(IReadOnlyList<ShaderBinding> Bindings, IReadOnlyList<ShaderStruct> Structs);

/// <summary>
/// Extracts the shader metadata the engine needs from the reflection JSON
/// emitted by <c>slangc -reflection-json</c>. This replaces the old WGSL
/// regex parser: Slang computes the binding layout and the engine reads it,
/// so the .slang source stays the single source of truth.
/// </summary>
internal static class SlangShaderReflection
{
    public static IReadOnlyList<ShaderEntryPoint> DetectEntryPoints(JsonDocument json)
    {
        if (!json.RootElement.TryGetProperty("entryPoints", out var entryPoints))
            return [];

        var result = new List<ShaderEntryPoint>();
        foreach (var entry in entryPoints.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            var stage = entry.GetProperty("stage").GetString();
            if (name is null || stage is null)
                continue;
            if (!Enum.TryParse<ShaderStageKind>(stage, ignoreCase: true, out var kind))
                continue;
            result.Add(new ShaderEntryPoint(name, kind));
        }

        return result;
    }

    public static SlangBindingsAndStructs DetectBindingsAndStructs(JsonDocument json)
    {
        var bindings = new List<ShaderBinding>();
        var structs = new List<ShaderStruct>();

        if (json.RootElement.TryGetProperty("parameters", out var parameters))
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var name = parameter.GetProperty("name").GetString();
                if (name is null)
                    continue;

                var binding = parameter.GetProperty("binding");
                if (binding.GetProperty("kind").GetString() != "descriptorTableSlot")
                    continue; // varyings and bare globals are not engine-managed resources

                var group = binding.TryGetProperty("space", out var space) ? space.GetInt32() : 0;
                var slot = checked((uint)binding.GetProperty("index").GetInt32());
                var type = parameter.GetProperty("type");
                var typeKind = type.GetProperty("kind").GetString();

                switch (typeKind)
                {
                    case "constantBuffer":
                    {
                        var elementType = type.GetProperty("elementType");
                        var elementKind = elementType.GetProperty("kind").GetString();
                        if (elementKind == "struct")
                        {
                            var structName = elementType.GetProperty("name").GetString()!;
                            bindings.Add(new ShaderBinding(group, slot, ShaderBindingKind.UniformBuffer, name, structName));
                            structs.Add(ParseStruct(elementType));
                        }
                        else
                        {
                            // A raw matrix/vector constant buffer (e.g. the model matrix).
                            bindings.Add(new ShaderBinding(group, slot, ShaderBindingKind.UniformBuffer, name, MapType(elementType)));
                        }
                        break;
                    }
                    case "resource":
                    {
                        var baseShape = type.GetProperty("baseShape").GetString();
                        switch (baseShape)
                        {
                            case "structuredBuffer":
                            {
                                var resultType = type.GetProperty("resultType");
                                var structName = resultType.TryGetProperty("name", out var elementName)
                                    ? elementName.GetString()!
                                    : "array";
                                bindings.Add(new ShaderBinding(group, slot, ShaderBindingKind.ReadOnlyStorageBuffer, name, $"array<{structName}>"));
                                break;
                            }
                            case "texture2D":
                            {
                                var resultType = type.GetProperty("resultType");
                                var isDepth = resultType.GetProperty("kind").GetString() == "scalar";
                                bindings.Add(new ShaderBinding(group, slot, ShaderBindingKind.Texture, name,
                                    isDepth ? "texture_depth_2d" : "texture_2d<f32>"));
                                break;
                            }
                        }
                        break;
                    }
                    case "samplerState":
                        bindings.Add(new ShaderBinding(group, slot, ShaderBindingKind.Sampler, name, "sampler"));
                        break;
                }
            }
        }

        return new SlangBindingsAndStructs(
            [.. bindings.OrderBy(b => b.Group).ThenBy(b => b.Slot)],
            structs);
    }

    public static IReadOnlyList<ShaderTechnique> DetectTechniques(IReadOnlyList<ShaderEntryPoint> entryPoints)
    {
        var vertices = entryPoints.Where(e => e.Stage == ShaderStageKind.Vertex).ToList();
        var fragments = entryPoints.Where(e => e.Stage == ShaderStageKind.Fragment)
            .ToDictionary(e => e.Name, StringComparer.Ordinal);

        var techniques = new List<ShaderTechnique>();
        foreach (var vertex in vertices)
        {
            if (!vertex.Name.StartsWith("vs_", StringComparison.Ordinal))
                continue;

            var fragmentName = "fs_" + vertex.Name[3..];
            if (fragments.TryGetValue(fragmentName, out var fragment))
            {
                var techniqueName = vertex.Name[3..];
                if (techniqueName.Length > 0)
                    techniqueName = char.ToUpperInvariant(techniqueName[0]) + techniqueName[1..];
                techniques.Add(new ShaderTechnique(techniqueName, vertex.Name, fragment.Name));
            }
        }

        return techniques;
    }

    private static ShaderStruct ParseStruct(JsonElement structType)
    {
        var name = structType.GetProperty("name").GetString() ?? string.Empty;
        var fields = new List<ShaderStructField>();

        if (structType.TryGetProperty("fields", out var fieldsElement))
        {
            foreach (var field in fieldsElement.EnumerateArray())
            {
                var fieldName = field.GetProperty("name").GetString();
                if (fieldName is null)
                    continue;
                fields.Add(new ShaderStructField(fieldName, MapType(field.GetProperty("type"))));
            }
        }

        return new ShaderStruct(name, fields);
    }

    /// <summary>Maps a Slang reflection type node back to the WGSL-style type string the packer expects.</summary>
    private static string MapType(JsonElement type) => type.GetProperty("kind").GetString() switch
    {
        "scalar" => type.GetProperty("scalarType").GetString() switch
        {
            "float32" => "f32",
            "float16" => "f16",
            "uint32" => "u32",
            "uint64" => "u64",
            "int32" => "i32",
            "int64" => "i64",
            "bool" => "bool",
            var scalar => scalar ?? "unknown"
        },
        "vector" => $"vec{type.GetProperty("elementCount").GetInt32()}<{MapType(type.GetProperty("elementType"))}>",
        "matrix" => $"mat{type.GetProperty("rowCount").GetInt32()}x{type.GetProperty("columnCount").GetInt32()}<{MapType(type.GetProperty("elementType"))}>",
        var kind => kind ?? "unknown"
    };
}
