using System.Numerics;
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
/// A shader source loaded from a file shipped with the application.
/// <c>#include</c> directives are resolved at load time
/// (<see cref="ShaderPreprocessor"/>), and the flattened source is reflected
/// (<see cref="ShaderReflection"/>) into entry points, bind-group
/// declarations, struct definitions and named techniques — so the WGSL is the
/// single source of truth for the pipeline layout and the material
/// parameters, and the <see cref="UniformPacker"/> derives the uniform layout
/// from it.
/// </summary>
public sealed class Shader
{
    public string Path { get; }

    public string FilePath { get; }

    public string Name { get; }

    /// <summary>The flattened source (includes resolved), ready for compilation.</summary>
    public string Source { get; }

    public IReadOnlyList<ShaderEntryPoint> EntryPoints { get; }

    /// <summary>Every resource binding declared by the shader, sorted by group then slot.</summary>
    public IReadOnlyList<ShaderBinding> Bindings { get; }

    /// <summary>Every struct definition in the flattened source.</summary>
    public IReadOnlyList<ShaderStruct> Structs { get; }

    /// <summary>Render passes (vertex/fragment pairs) the shader exposes.</summary>
    public IReadOnlyList<ShaderTechnique> Techniques { get; }

    /// <summary>
    /// Fields of the material uniform struct (the struct referenced by a
    /// group &gt;= 1 uniform binding whose name contains "Material"). These are
    /// the parameters a <see cref="Material"/> can set, packed by
    /// <see cref="UniformPacker"/> into the struct's exact layout.
    /// </summary>
    public IReadOnlyList<ShaderStructField> MaterialFields { get; }

    /// <summary>The material fields mapped to CLR types, for parameter validation.</summary>
    public IReadOnlyList<ShaderParameterDefinition> Parameters { get; }

    private Shader(string requestedPath, FilePath filePath, string source)
    {
        Path = requestedPath;
        FilePath = filePath.FullName;
        Source = source;
        Name = filePath.GetNameWithoutExtension() ?? string.Empty;
        EntryPoints = ShaderReflection.DetectEntryPoints(source);
        Bindings = ShaderReflection.DetectBindings(source);
        Structs = ShaderReflection.DetectStructs(source);
        Techniques = ShaderReflection.DetectTechniques(EntryPoints);
        MaterialFields = FindMaterialFields();
        Parameters =
        [
            .. MaterialFields
                .Select(field => ShaderParameter.TryParseParameter(field.Name, field.Type))
                .Where(parameter => parameter != null)
                .Select(parameter => parameter!)
        ];
    }

    public ShaderEntryPoint GetEntryPoint(string name)
    {
        return EntryPoints.FirstOrDefault(entry => entry.Name == name)
               ?? throw new InvalidOperationException(
                   $"Shader '{Name}' does not contain an entry point named '{name}'.");
    }

    public ShaderTechnique GetTechnique(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = "Main";

        return Techniques.FirstOrDefault(technique => technique.Name == name)
               ?? throw new InvalidOperationException(
                   $"Shader '{Name}' has no technique named '{name}'. Available: {string.Join(", ", Techniques.Select(t => t.Name))}");
    }

    public static Shader Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fs = FileSystem.Content;
        var candidates = FileSystemService.IsRooted(path)
            ? [fs.ToFilePath(path)]
            : new[] { fs.ToFilePath(path), fs.ToWorkingDirectoryPath(path) };

        foreach (var candidate in candidates)
        {
            if (fs.FileExists(candidate))
            {
                var source = ShaderPreprocessor.Preprocess(fs, candidate);
                return new Shader(path, candidate, source);
            }
        }

        throw new FileNotFoundException(
            $"Shader file '{path}' was not found.",
            path);
    }

    private IReadOnlyList<ShaderStructField> FindMaterialFields()
    {
        var materialStruct = Bindings
            .Where(binding => binding.Kind == ShaderBindingKind.UniformBuffer && binding.Group >= 1)
            .Select(binding => Structs.FirstOrDefault(struct_ => struct_.Name == binding.TypeName))
            .FirstOrDefault(struct_ => struct_?.Name.Contains("Material", StringComparison.Ordinal) == true);

        return materialStruct?.Fields ?? [];
    }
}
