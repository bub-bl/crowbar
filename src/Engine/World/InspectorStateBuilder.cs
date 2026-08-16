using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using Crowbar.UI;

namespace Crowbar.Engine;

/// <summary>
/// Serializes an entity into the flat snapshot the Inspector panel displays:
/// one collapsible transform section plus one collapsible section per
/// component. Unlike a hand-written per-component switch, the component
/// sections are built by reflecting the component's public properties marked
/// with <see cref="PropertyAttribute"/>; each property carries the canonical
/// CLR type name of its value and the UI resolves that name to an editor
/// component (a material still expands to a header plus its shader
/// parameters). The snapshot is plain UI data
/// (<see cref="EditorInspectorState"/>), so the UI assembly never learns about
/// engine component types.
///
/// This lives in the engine (rather than the editor host, like
/// <c>ExplorerTreeBuilder</c>) because it reflects engine types and the engine
/// already depends on the UI assembly.
/// </summary>
public static class InspectorStateBuilder
{
    public static void Publish(Entity? entity)
    {
        if (entity is null || !entity.IsValid)
        {
            EditorInspectorState.Publish(null, []);
            return;
        }

        EditorInspectorState.Publish(entity.Name, Build(entity));
    }

    /// <summary>Builds the sections for a living entity, without publishing them.</summary>
    public static IReadOnlyList<EditorInspectorState.Section> Build(Entity entity)
    {
        var sections = new List<EditorInspectorState.Section>();

        // The entity's spatial transform (its first TransformComponent) gets the
        // dedicated, always-present "Transform" section.
        var transform = entity.GetComponent<TransformComponent>();
        if (transform is not null)
            sections.Add(new EditorInspectorState.Section("transform", "Transform", null, DescribeTransform(transform.Local)));

        // A spatial component is both the transform source and a component in
        // its own right (MeshRenderer, PointLight, …): the transform section
        // shows its Local, while this loop shows its own marked properties.
        // Infrastructure-declared properties (Local, World, Entity, …) are
        // excluded by DescribeComponent, so nothing is duplicated.
        foreach (var component in entity.Components)
        {
            if (!component.IsValid)
                continue;

            var properties = DescribeComponent(component);
            if (properties.Count == 0)
                continue;

            sections.Add(new EditorInspectorState.Section(
                component.GetType().Name, component.GetType().Name, IconFor(component), properties));
        }

        return sections;
    }

    // ---- Dynamic reflection -------------------------------------------------

    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    /// <summary>
    /// Resolves the component's inspectable properties: public instance
    /// properties marked with <see cref="PropertyAttribute"/>, excluding the
    /// lifecycle/attachment plumbing declared on the component base types (and
    /// the spatial <see cref="TransformComponent.Local"/>, which is already
    /// shown in the transform section).
    /// </summary>
    private static IReadOnlyList<EditorInspectorState.Property> DescribeComponent(Component component)
    {
        var properties = new List<EditorInspectorState.Property>();
        foreach (var property in component.GetType()
                     .GetProperties(InstancePublic)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() is not null)
                     .Where(p => p.IsDefined(typeof(PropertyAttribute), inherit: true))
                     .Where(p => !IsInfrastructure(p))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            DescribeValue(property.Name, property.PropertyType, property.GetValue(component), 0, properties);
        }

        return properties;
    }

    private static bool IsInfrastructure(PropertyInfo property) =>
        property.DeclaringType == typeof(Component) ||
        property.DeclaringType == typeof(WorldObject) ||
        property.DeclaringType == typeof(TransformComponent);

    /// <summary>Appends the flat rows for one property value, keyed by its CLR type.</summary>
    private static void DescribeValue(string name, Type type, object? value, int indent,
        List<EditorInspectorState.Property> output)
    {
        switch (value)
        {
            case Vector2 vector: output.Add(Scalar(name, typeof(Vector2), FormatVector2(vector), indent)); return;
            case Vector3 vector: output.Add(Scalar(name, typeof(Vector3), FormatVector3(vector), indent)); return;
            case Vector4 vector: output.Add(Scalar(name, typeof(Vector4), FormatVector4(vector), indent)); return;
            case Transform transform:
                output.Add(Scalar("Position", typeof(Vector3), FormatVector3(transform.Position), indent));
                output.Add(Scalar("Rotation", typeof(Vector3), FormatVector3(ToEulerDegrees(transform.Rotation)), indent));
                output.Add(Scalar("Scale", typeof(Vector3), FormatVector3(transform.Scale), indent));
                return;
            case Material material:
                AddMaterial(name, material, indent, output);
                return;
            default:
                output.Add(new EditorInspectorState.Property(name, TypeNameOf(type), FormatValue(value), Indent: indent));
                return;
        }
    }

    private static void AddMaterial(string name, Material material, int indent,
        List<EditorInspectorState.Property> output)
    {
        output.Add(new EditorInspectorState.Property(name, TypeNameOf(typeof(Material)), material.Name, Indent: indent));

        // Each shader parameter resolves its own editor from its CLR type, so
        // the material editor stays data-driven instead of listing fields.
        foreach (var (parameterName, parameter) in material.Values)
            DescribeValue(Humanize(parameterName), ValueType(parameter), ValueOf(parameter), indent + 1, output);
    }

    private static Type ValueType(ShaderParameter parameter) => parameter.Value?.GetType() ?? typeof(object);

    private static object? ValueOf(ShaderParameter parameter) => parameter.Value;

    // ---- Transform ----------------------------------------------------------

    private static IReadOnlyList<EditorInspectorState.Property> DescribeTransform(Transform transform) =>
    [
        Scalar("Position", typeof(Vector3), FormatVector3(transform.Position)),
        Scalar("Rotation", typeof(Vector3), FormatVector3(ToEulerDegrees(transform.Rotation))),
        Scalar("Scale", typeof(Vector3), FormatVector3(transform.Scale))
    ];

    private static Vector3 ToEulerDegrees(Rotation rotation)
    {
        var angles = rotation.Angles();
        return new Vector3(angles.Pitch, angles.Yaw, angles.Roll);
    }

    // ---- Row builders -------------------------------------------------------

    private static EditorInspectorState.Property Scalar(string name, Type type, string value, int indent = 0) =>
        new(name, TypeNameOf(type), value, Indent: indent);

    private static string FormatVector2(Vector2 value) =>
        $"{FormatFloat(value.X)}, {FormatFloat(value.Y)}";

    private static string FormatVector3(Vector3 value) =>
        $"{FormatFloat(value.X)}, {FormatFloat(value.Y)}, {FormatFloat(value.Z)}";

    private static string FormatVector4(Vector4 value) =>
        $"{FormatFloat(value.X)}, {FormatFloat(value.Y)}, {FormatFloat(value.Z)}, {FormatFloat(value.W)}";

    // ---- Formatting ---------------------------------------------------------

    /// <summary>The canonical type identity the UI resolves to an editor component.</summary>
    private static string TypeNameOf(Type type) => type.FullName ?? type.Name;

    private static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        float f => FormatFloat(f),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        uint u => u.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        byte b => b.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s,
        Model model => model.Name,
        Material material => material.Name,
        Shader shader => shader.Name,
        Entity entity => entity.Name,
        Enum e => e.ToString(),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Humanizes a C# member name for the label ("MaxDistance" → "Max Distance").</summary>
    private static string Humanize(string name)
    {
        if (name.Length == 0)
            return name;

        var builder = new StringBuilder(name.Length + 4);
        builder.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                builder.Append(' ');
            builder.Append(name[i]);
        }

        return builder.ToString();
    }

    private static string? IconFor(Component component) => component switch
    {
        Light => "Solar/devices/Bold/lightbulb",
        MeshRenderer => "Solar/ui/Bold/box-minimalistic",
        _ => "Solar/ui/Bold/box"
    };
}
