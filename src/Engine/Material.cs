using System.Numerics;
using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// Values supplied to a shader for a renderable object: scalar/vector/matrix
/// parameters validated against the shader's material struct, texture slots
/// for the shader's texture bindings, and the technique (render pass) to use.
/// The renderer packs the parameters with <see cref="UniformPacker"/> and
/// uploads the textures.
/// </summary>
public sealed class Material
{
    private readonly Dictionary<string, ShaderParameter> _values = [];
    private readonly Dictionary<string, Texture2D> _textures = [];

    public string Name { get; }
    public Shader Shader { get; }

    /// <summary>Render pass to use; defaults to the shader's "Main" technique.</summary>
    public string Technique { get; }

    public IReadOnlyDictionary<string, ShaderParameter> Values => _values;
    public IReadOnlyDictionary<string, Texture2D> Textures => _textures;

    private Material(string name, Shader shader, string technique)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A material needs a name.", nameof(name))
            : name;
        Shader = shader ?? throw new ArgumentNullException(nameof(shader));
        Technique = string.IsNullOrWhiteSpace(technique) ? "Main" : technique;
    }

    public static Material FromShader(string shaderName, string technique = "Main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderName);

        var shaderPath = shaderName;

        if (!PathUtil.HasExtension(shaderPath))
        {
            shaderPath += ".wgsl";
            if (!shaderPath.Contains('/'))
                shaderPath = PathUtil.Combine("Shaders", shaderPath);
        }

        var shader = Shader.Load(shaderPath);
        shader.GetTechnique(technique); // validate the technique exists now, not at render time
        return new Material(shader.Name, shader, technique);
    }

    public Material Set(string parameterName, ShaderParameter value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        var parameter = Shader.Parameters.FirstOrDefault(p => p.Name == parameterName)
                        ?? throw new ArgumentException(
                            $"Shader '{Shader.Name}' has no parameter named '{parameterName}'. Available: {string.Join(", ", Shader.Parameters.Select(p => p.Name))}",
                            nameof(parameterName));

        _values[parameterName] = value;
        return this;
    }

    /// <summary>Binds a texture to one of the shader's texture slots (by the WGSL variable name).</summary>
    public Material SetTexture(string slotName, Texture2D texture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ArgumentNullException.ThrowIfNull(texture);

        var binding = Shader.Bindings.FirstOrDefault(b => b.Kind == ShaderBindingKind.Texture && b.VariableName == slotName)
                      ?? throw new ArgumentException(
                          $"Shader '{Shader.Name}' has no texture slot named '{slotName}'. Available: {string.Join(", ", Shader.Bindings.Where(b => b.Kind == ShaderBindingKind.Texture).Select(b => b.VariableName))}",
                          nameof(slotName));

        _textures[binding.VariableName] = texture;
        return this;
    }

    public bool TryGet<T>(string parameterName, out T value)
    {
        if (_values.TryGetValue(parameterName, out var raw) && TryGetValue(raw, out T typed))
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public T Get<T>(string parameterName, T fallback = default!) =>
        TryGet(parameterName, out T value) ? value : fallback;

    /// <summary>
    /// A blue lit material usable with any of the engine's lit shaders. Only
    /// parameters the shader actually declares are set, so the same defaults
    /// work for Mesh and Pbr alike.
    /// </summary>
    public static Material CreateDefault(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        var material = new Material("Default", shader, "Main");
        if (shader.Parameters.Any(p => p.Name == "color"))
            material.Set("color", new Vector4(0.2f, 0.6f, 1.0f, 1.0f));
        if (shader.Parameters.Any(p => p.Name == "metallic"))
            material.Set("metallic", 0.1f);
        if (shader.Parameters.Any(p => p.Name == "roughness"))
            material.Set("roughness", 0.7f);
        if (shader.Parameters.Any(p => p.Name == "occlusion"))
            material.Set("occlusion", 1f);
        if (shader.Parameters.Any(p => p.Name == "emissive"))
            material.Set("emissive", 0f);
        return material;
    }

    private static bool TryGetValue<T>(ShaderParameter parameter, out T value)
    {
        if (parameter.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
