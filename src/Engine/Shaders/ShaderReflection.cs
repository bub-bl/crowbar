using System.Text.RegularExpressions;

namespace Crowbar.Engine;

/// <summary>Kind of a resource declared by a WGSL binding.</summary>
public enum ShaderBindingKind
{
    UniformBuffer,
    ReadOnlyStorageBuffer,
    Texture,
    Sampler
}

/// <summary>
/// One <c>@group(N) @binding(M) var&lt;...&gt; name: Type;</c> declaration.
/// <see cref="TypeName"/> is the struct name for uniform bindings, the matrix
/// type for a raw <c>mat4</c> uniform, or the texture/sampler type.
/// </summary>
public sealed record ShaderBinding(int Group, uint Slot, ShaderBindingKind Kind, string VariableName, string TypeName);

/// <summary>One field of a WGSL struct: name plus WGSL type (e.g. <c>vec4&lt;f32&gt;</c>).</summary>
public sealed record ShaderStructField(string Name, string Type);

/// <summary>A WGSL struct definition.</summary>
public sealed record ShaderStruct(string Name, IReadOnlyList<ShaderStructField> Fields);

/// <summary>
/// A render pass: a vertex/fragment entry-point pair. Like an .fx technique —
/// <c>vs_main</c>/<c>fs_main</c> becomes "Main", <c>vs_outline</c>/<c>fs_outline</c>
/// becomes "Outline", and a material selects one by name.
/// </summary>
public sealed record ShaderTechnique(string Name, string VertexEntryPoint, string FragmentEntryPoint);

/// <summary>
/// Parses the WGSL subset the engine relies on: entry points, bind-group
/// declarations and struct definitions. The reflection makes the shader
/// source the single source of truth for pipeline bindings and material
/// parameters — nothing is declared twice anymore.
/// </summary>
internal static partial class ShaderReflection
{
    public static IReadOnlyList<ShaderEntryPoint> DetectEntryPoints(string source)
    {
        return
        [
            .. EntryPointPattern.Matches(source)
                .Select(match => new ShaderEntryPoint(
                    match.Groups["name"].Value,
                    Enum.Parse<ShaderStageKind>(match.Groups["stage"].Value, ignoreCase: true)))
        ];
    }

    public static IReadOnlyList<ShaderBinding> DetectBindings(string source)
    {
        var bindings = new List<ShaderBinding>();
        foreach (Match match in BindingPattern.Matches(source))
        {
            var kind = Classify(match.Groups["access"].Value, match.Groups["type"].Value);
            if (kind is null)
                continue;

            bindings.Add(new ShaderBinding(
                int.Parse(match.Groups["group"].Value),
                uint.Parse(match.Groups["slot"].Value),
                kind.Value,
                match.Groups["name"].Value,
                match.Groups["type"].Value));
        }

        return bindings.OrderBy(b => b.Group).ThenBy(b => b.Slot).ToList();
    }

    public static IReadOnlyList<ShaderStruct> DetectStructs(string source)
    {
        var structs = new List<ShaderStruct>();
        foreach (Match match in StructPattern.Matches(source))
            structs.Add(ParseStruct(match));

        return structs;
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

    private static ShaderBindingKind? Classify(string access, string type)
    {
        if (access == "uniform") return ShaderBindingKind.UniformBuffer;
        if (access == "read" || access == "read_write") return ShaderBindingKind.ReadOnlyStorageBuffer;
        if (type == "sampler") return ShaderBindingKind.Sampler;
        if (type.StartsWith("texture_", StringComparison.Ordinal)) return ShaderBindingKind.Texture;
        return null;
    }

    private static ShaderStruct ParseStruct(Match match)
    {
        var fields = new List<ShaderStructField>();
        foreach (var rawLine in match.Groups["body"].Value.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd(',').Trim();
            if (line.Length == 0)
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = line[..colon].Trim();
            var type = line[(colon + 1)..].Trim();
            if (name.Length > 0 && type.Length > 0)
                fields.Add(new ShaderStructField(name, type));
        }

        return new ShaderStruct(match.Groups["name"].Value, fields);
    }

    [GeneratedRegex(@"(?m)^\s*@(?<stage>vertex|fragment|compute)\s+fn\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex EntryPointPattern { get; }

    [GeneratedRegex(
        @"@group\((?<group>\d+)\)\s*@binding\((?<slot>\d+)\)\s*var(?:<(?<access>[^>]+)>)?\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?<type>[A-Za-z0-9_<>]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BindingPattern { get; }

    [GeneratedRegex(@"(?s)struct\s+(?<name>[A-Za-z_]\w*)\s*\{(?<body>.*?)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex StructPattern { get; }
}
