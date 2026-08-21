using System.Numerics;
using System.Text.Json;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;

namespace Crowbar.Engine;

public enum ShaderStageKind
{
    Vertex,
    Fragment,
    Compute
}

public sealed record ShaderEntryPoint(string Name, ShaderStageKind Stage);

public readonly union ShaderParameter(float, Vector2, Vector3, Vector4, Matrix4x4, int, uint, bool)
{
    public string TypeName => this switch
    {
        float => "f32",
        int => "i32",
        uint => "u32",
        bool => "bool",
        Vector2 => "vec2f",
        Vector3 => "vec3f",
        Vector4 => "vec4f",
        Matrix4x4 => "mat4f",
        _ => throw new NotImplementedException()
    };

    public static ShaderParameterDefinition? TryParseParameter(string name, string type)
    {
        return type switch
        {
            "f32" => new ShaderParameterDefinition(name, typeof(float)),
            "i32" => new ShaderParameterDefinition(name, typeof(int)),
            "u32" => new ShaderParameterDefinition(name, typeof(uint)),
            "bool" => new ShaderParameterDefinition(name, typeof(bool)),
            "vec2<f32>" or "vec2f" => new ShaderParameterDefinition(name, typeof(Vector2)),
            "vec3<f32>" or "vec3f" => new ShaderParameterDefinition(name, typeof(Vector3)),
            "vec4<f32>" or "vec4f" => new ShaderParameterDefinition(name, typeof(Vector4)),
            "mat4x4<f32>" or "mat4f" => new ShaderParameterDefinition(name, typeof(Matrix4x4)),
            _ => null,
        };
    }
}

public sealed record ShaderParameterDefinition(string Name, Type Type);

/// <summary>
/// A compiled shader: the WGSL emitted by <c>slangc</c> plus the reflection
/// sidecar (<c>-reflection-json</c>) that describes its entry points, bind
/// groups, struct definitions and material parameters. Slang is the single
/// source of truth — the engine never re-derives the layout from source text.
///
/// Shaders are cached through the global <see cref="Global.ResourceLibrary"/>
/// like <see cref="Model"/> and <see cref="Texture2D"/>: loading the same
/// shader twice returns the same instance. Shaders are compiled at build time
/// (slangc) and never hot-reloaded, so the loaded instance never goes stale;
/// <see cref="Invalidate"/> and <see cref="ClearCache"/> exist for tooling
/// and tests. This cache is what keeps material restore cheap: without it,
/// every undo/redo re-read the WGSL file and its reflection sidecar once per
/// material.
/// </summary>
[AssetType("wgsl")]
public sealed class Shader : ResourceFile
{
    /// <summary>The canonical resolved path of the file, for diagnostics.</summary>
    public string FilePath { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The compiled WGSL source, ready for the WebGPU backend.</summary>
    public string Source { get; private set; } = string.Empty;

    public IReadOnlyList<ShaderEntryPoint> EntryPoints { get; private set; } = [];

    /// <summary>Every resource binding declared by the shader, sorted by group then slot.</summary>
    public IReadOnlyList<ShaderBinding> Bindings { get; private set; } = [];

    /// <summary>Every struct definition the shader declares.</summary>
    public IReadOnlyList<ShaderStruct> Structs { get; private set; } = [];

    /// <summary>Render passes (vertex/fragment pairs) the shader exposes.</summary>
    public IReadOnlyList<ShaderTechnique> Techniques { get; private set; } = [];

    /// <summary>
    /// Fields of the material uniform struct (the uniform binding in group &gt;= 1
    /// whose struct name contains "Material"). These are the parameters a
    /// <see cref="Material"/> can set, packed by <see cref="UniformPacker"/>
    /// into the struct's exact layout.
    /// </summary>
    public IReadOnlyList<ShaderStructField> MaterialFields { get; private set; } = [];

    /// <summary>The material fields mapped to CLR types, for parameter validation.</summary>
    public IReadOnlyList<ShaderParameterDefinition> Parameters { get; private set; } = [];

    /// <summary>Allocated by the library, then populated through <see cref="Load"/>.</summary>
    private Shader()
    {
    }

    public ShaderEntryPoint GetEntryPoint(string name)
    {
        return EntryPoints.FirstOrDefault(entry => entry.Name == name)
               ?? throw new InvalidOperationException(
                   $"Shader '{Name}' does not contain an entry point named '{name}'.");
    }

    /// <summary>
    /// Builds the bind-group layouts this shader requires from its reflected
    /// bindings, one layout per group ordered by slot. Slangc's reflection
    /// reports each binding's group (space), slot (index), kind and texture
    /// shape, so the pipeline layout derives from the shader instead of being
    /// hand-coded per pass. Stage visibility is conservative — uniform and
    /// storage buffers are exposed to both stages, textures and samplers to
    /// the fragment stage — which WebGPU accepts as a superset of any narrower
    /// usage, and depth textures (reflected as <c>texture_depth_2d</c>) map to
    /// the depth binding type.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<BindGroupLayoutBinding>> BuildBindGroupLayouts()
    {
        return
        [
            .. Bindings
                .GroupBy(binding => binding.Group)
                .OrderBy(group => group.Key)
                .Select(group => (IReadOnlyList<BindGroupLayoutBinding>)group
                    .OrderBy(binding => binding.Slot)
                    .Select(binding => new BindGroupLayoutBinding
                    {
                        Slot = binding.Slot,
                        Type = binding.Kind switch
                        {
                            ShaderBindingKind.UniformBuffer => BindingType.UniformBuffer,
                            ShaderBindingKind.ReadOnlyStorageBuffer => BindingType.ReadOnlyStorageBuffer,
                            ShaderBindingKind.Texture =>
                                binding.TypeName == "texture_depth_2d" ? BindingType.DepthTexture : BindingType.Texture,
                            ShaderBindingKind.Sampler => BindingType.Sampler,
                            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding.Kind, null)
                        },
                        Stages = binding.Kind is ShaderBindingKind.Texture or ShaderBindingKind.Sampler
                            ? ShaderStage.Fragment
                            : ShaderStage.Vertex | ShaderStage.Fragment
                    })
                    .ToList())
        ];
    }

    public ShaderTechnique GetTechnique(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = "Main";

        return Techniques.FirstOrDefault(technique => technique.Name == name)
               ?? throw new InvalidOperationException(
                   $"Shader '{Name}' has no technique named '{name}'. Available: {string.Join(", ", Techniques.Select(t => t.Name))}");
    }

    /// <summary>
    /// Reads the WGSL file and its reflection sidecar and populates this
    /// instance. The library allocates the instance, assigns
    /// <see cref="ResourceFile.Path"/> and calls this; loading the same shader
    /// twice returns the same instance through the shared
    /// <see cref="Global.ResourceLibrary"/> cache.
    /// </summary>
    public override void Load()
    {
        var fs = FileSystem.Content;
        var candidates = FileSystemService.IsRooted(Path)
            ? [fs.ToFilePath(Path)]
            : new[] { fs.ToFilePath(Path), fs.ToWorkingDirectoryPath(Path) };

        foreach (var candidate in candidates)
        {
            if (!fs.FileExists(candidate))
                continue;

            var source = fs.ReadAllText(candidate);
            var sidecar = candidate.ChangeExtension(".slang.json");
            if (!fs.FileExists(sidecar))
                throw new FileNotFoundException(
                    $"Shader reflection sidecar '{sidecar}' was not found next to '{candidate}'.",
                    sidecar.FullName);

            using var json = JsonDocument.Parse(fs.ReadAllText(sidecar));
            EntryPoints = SlangShaderReflection.DetectEntryPoints(json);
            (Bindings, Structs) = SlangShaderReflection.DetectBindingsAndStructs(json);
            Techniques = SlangShaderReflection.DetectTechniques(EntryPoints);
            FilePath = candidate.FullName;
            Name = candidate.GetNameWithoutExtension() ?? string.Empty;
            Source = source;
            MaterialFields = FindMaterialFields();
            Parameters =
            [
                .. MaterialFields
                    .Select(field => ShaderParameter.TryParseParameter(field.Name, field.Type))
                    .Where(parameter => parameter != null)
                    .Select(parameter => parameter!)
            ];
            IsValid = true;
            return;
        }

        throw new FileNotFoundException(
            $"Shader file '{Path}' was not found.",
            Path);
    }

    public static Shader Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.Load<Shader>(path);
    }

    /// <summary>Discards the cached shader at <paramref name="path"/> so the next load re-reads it.</summary>
    public static void Invalidate(string path) => ResourceLibrary.Invalidate<Shader>(path);

    /// <summary>Discards every cached shader.</summary>
    public static void ClearCache() => ResourceLibrary.Clear<Shader>();

    private IReadOnlyList<ShaderStructField> FindMaterialFields()
    {
        var materialStruct = Bindings
            .Where(binding => binding.Kind == ShaderBindingKind.UniformBuffer && binding.Group >= 1)
            .Select(binding => Structs.FirstOrDefault(struct_ => struct_.Name == binding.TypeName))
            .FirstOrDefault(struct_ => struct_?.Name.Contains("Material", StringComparison.Ordinal) == true);

        return materialStruct?.Fields ?? [];
    }
}
