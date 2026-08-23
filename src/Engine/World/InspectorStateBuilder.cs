using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using Crowbar.UI;

namespace Crowbar.Engine;

/// <summary>
/// Serializes an entity into the flat snapshot the Inspector panel displays and
/// applies the edits the panel sends back. One collapsible transform section
/// plus one collapsible section per component; the component sections are built
/// by reflecting the public properties marked with <see cref="PropertyAttribute"/>.
/// Each property carries the canonical CLR type name of its value (resolved to
/// an editor component by the UI) and a stable write-back key that
/// <see cref="ApplyEdit"/> resolves back to the source property, transform field
/// or material shader parameter. The snapshot is plain UI data
/// (<see cref="EditorInspectorState"/>), so the UI assembly never learns about
/// engine component types.
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

    /// <summary>
    /// Applies one write-back edit to the entity. The key resolves to a
    /// transform field (<c>transform.position</c>), a component property
    /// (<c>PointLight.Intensity</c>) or a material shader parameter
    /// (<c>MeshRenderer.Material.metallic</c>). Malformed keys and values are
    /// ignored, so a bad keystroke never crashes the host. A successfully
    /// applied edit marks the owning level dirty (<see cref="Level.MarkDirty"/>).
    /// </summary>
    public static void ApplyEdit(Entity entity, string key, string value)
    {
        if (entity is null || !entity.IsValid || string.IsNullOrEmpty(key))
            return;

        var applied = false;
        if (key.StartsWith("transform.", StringComparison.Ordinal))
        {
            applied = ApplyTransformEdit(entity, key["transform.".Length..], value);
        }
        else
        {
            var parts = key.Split('.');
            if (parts.Length == 2)
            {
                var component = entity.Components.FirstOrDefault(c =>
                    c.GetType().Name.Equals(parts[0], StringComparison.Ordinal));
                if (component is not null)
                {
                    var property = component.GetType().GetProperty(parts[1], InstancePublic);
                    if (property?.CanWrite == true && TryParseValue(value, property.PropertyType, out var parsed))
                    {
                        property.SetValue(component, parsed);
                        applied = true;
                    }
                }
            }
            else if (parts.Length == 3 && parts[1].Equals("Material", StringComparison.Ordinal))
            {
                var component = entity.Components.FirstOrDefault(c =>
                    c.GetType().Name.Equals(parts[0], StringComparison.Ordinal));
                if (component is not null)
                {
                    var materialProperty = component.GetType().GetProperty("Material", InstancePublic);
                    if (materialProperty?.GetValue(component) is Material material)
                    {
                        var parameterType = material.Shader.Parameters.FirstOrDefault(p => p.Name == parts[2])?.Type;
                        if (parameterType is not null && TryParseValue(value, parameterType, out var parsed))
                        {
                            material.Set(parts[2], ToShaderParameter(parsed!));
                            applied = true;
                        }
                    }
                }
            }
        }

        if (applied)
            entity.Level?.MarkDirty();
    }

    private static bool ApplyTransformEdit(Entity entity, string field, string value)
    {
        if (entity.GetComponent<TransformComponent>() is not { } transform)
            return false;

        switch (field)
        {
            case "position" when TryParseVector3(value, out var position):
                transform.Local = transform.Local.WithPosition(position);
                return true;
            case "scale" when TryParseVector3(value, out var scale):
                transform.Local = transform.Local.WithScale(scale);
                return true;
            case "rotation" when TryParseEulerDegrees(value, out var rotation):
                transform.Local = transform.Local.WithRotation(rotation);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseEulerDegrees(string value, out Rotation rotation)
    {
        if (TryParseVector3(value, out var degrees))
        {
            rotation = Rotation.From(new Angles(degrees.X, degrees.Y, degrees.Z));
            return true;
        }

        rotation = default;
        return false;
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
        var sectionId = component.GetType().Name;
        var properties = new List<EditorInspectorState.Property>();
        foreach (var property in component.GetType()
                     .GetProperties(InstancePublic)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() is not null)
                     .Where(p => p.IsDefined(typeof(PropertyAttribute), inherit: true))
                     .Where(p => !IsInfrastructure(p))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            DescribeValue(property.Name, property.PropertyType, property.GetValue(component), 0,
                $"{sectionId}.{property.Name}", properties);
        }

        return properties;
    }

    private static bool IsInfrastructure(PropertyInfo property) =>
        property.DeclaringType == typeof(Component) ||
        property.DeclaringType == typeof(WorldObject) ||
        property.DeclaringType == typeof(TransformComponent);

    /// <summary>Appends the flat rows for one property value, keyed by its CLR type.</summary>
    private static void DescribeValue(string name, Type type, object? value, int indent, string key,
        List<EditorInspectorState.Property> output)
    {
        switch (value)
        {
            case Vector2 vector: output.Add(Scalar(name, typeof(Vector2), FormatVector2(vector), indent, key)); return;
            case Vector3 vector: output.Add(Scalar(name, typeof(Vector3), FormatVector3(vector), indent, key)); return;
            case Vector4 vector: output.Add(Scalar(name, typeof(Vector4), FormatVector4(vector), indent, key)); return;
            case Material material:
                AddMaterial(name, material, indent, key, output);
                return;
            default:
                output.Add(new EditorInspectorState.Property(name, TypeNameOf(type), FormatValue(value), Indent: indent, Key: key));
                return;
        }
    }

    private static void AddMaterial(string name, Material material, int indent, string key,
        List<EditorInspectorState.Property> output)
    {
        // Read-only header: the material is a reference, not an editable value.
        output.Add(new EditorInspectorState.Property(name, TypeNameOf(typeof(Material)), material.Name, Indent: indent));

        // Each shader parameter resolves its own editor from its CLR type and
        // keeps its original parameter name in the write-back key.
        foreach (var (parameterName, parameter) in material.Values)
            DescribeValue(Humanize(parameterName), ValueType(parameter), ValueOf(parameter), indent + 1,
                $"{key}.{parameterName}", output);
    }

    private static Type ValueType(ShaderParameter parameter) => parameter.Value?.GetType() ?? typeof(object);

    private static object? ValueOf(ShaderParameter parameter) => parameter.Value;

    // ---- Transform ----------------------------------------------------------

    private static IReadOnlyList<EditorInspectorState.Property> DescribeTransform(Transform transform) =>
    [
        Scalar("Position", typeof(Vector3), FormatVector3(transform.Position), key: "transform.position"),
        Scalar("Rotation", typeof(Vector3), FormatVector3(ToEulerDegrees(transform.Rotation)), key: "transform.rotation"),
        Scalar("Scale", typeof(Vector3), FormatVector3(transform.Scale), key: "transform.scale")
    ];

    private static Vector3 ToEulerDegrees(Rotation rotation)
    {
        var angles = rotation.Angles();
        return new Vector3(angles.Pitch, angles.Yaw, angles.Roll);
    }

    // ---- Row builders -------------------------------------------------------

    private static EditorInspectorState.Property Scalar(string name, Type type, string value, int indent = 0, string key = "") =>
        new(name, TypeNameOf(type), value, Indent: indent, Key: key);

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

    // ---- Value parsing (write-back) -----------------------------------------

    private static bool TryParseValue(string text, Type type, out object? value)
    {
        try
        {
            if (type == typeof(float)) value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (type == typeof(double)) value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (type == typeof(int)) value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(uint)) value = uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(long)) value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(ulong)) value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(short)) value = short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(ushort)) value = ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(byte)) value = byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(sbyte)) value = sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (type == typeof(bool)) value = bool.Parse(text);
            else if (type == typeof(string)) value = text;
            else if (type == typeof(Vector2)) value = ParseVector2(text);
            else if (type == typeof(Vector3)) value = ParseVector3(text);
            else if (type == typeof(Vector4)) value = ParseVector4(text);
            else if (type.IsEnum) value = Enum.Parse(type, text, ignoreCase: true);
            else { value = null; return false; }

            return true;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryParseVector3(string value, out Vector3 result)
    {
        var parts = SplitComponents(value);
        if (parts.Length != 3 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            result = default;
            return false;
        }

        result = new Vector3(x, y, z);
        return true;
    }

    private static Vector2 ParseVector2(string text)
    {
        var parts = SplitComponents(text);
        return new Vector2(
            parts.Length > 0 ? ParseFloat(parts[0]) : 0f,
            parts.Length > 1 ? ParseFloat(parts[1]) : 0f);
    }

    private static Vector3 ParseVector3(string text)
    {
        var parts = SplitComponents(text);
        return new Vector3(
            parts.Length > 0 ? ParseFloat(parts[0]) : 0f,
            parts.Length > 1 ? ParseFloat(parts[1]) : 0f,
            parts.Length > 2 ? ParseFloat(parts[2]) : 0f);
    }

    private static Vector4 ParseVector4(string text)
    {
        var parts = SplitComponents(text);
        return new Vector4(
            parts.Length > 0 ? ParseFloat(parts[0]) : 0f,
            parts.Length > 1 ? ParseFloat(parts[1]) : 0f,
            parts.Length > 2 ? ParseFloat(parts[2]) : 0f,
            parts.Length > 3 ? ParseFloat(parts[3]) : 0f);
    }

    private static float ParseFloat(string text) =>
        float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string[] SplitComponents(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ShaderParameter ToShaderParameter(object value)
    {
        if (value is float f) return f;
        if (value is int i) return i;
        if (value is uint u) return u;
        if (value is bool b) return b;
        if (value is Vector2 v2) return v2;
        if (value is Vector3 v3) return v3;
        if (value is Vector4 v4) return v4;
        throw new InvalidOperationException($"Unsupported shader parameter type '{value.GetType().Name}'.");
    }

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

    /// <summary>
    /// The component's editor icon, declared through
    /// <see cref="ComponentIconAttribute"/> (inherited, so e.g. every light
    /// resolves the lightbulb declared on <see cref="Light"/>).
    /// </summary>
    private static string IconFor(Component component) =>
        component.GetType().GetCustomAttribute<ComponentIconAttribute>(inherit: true)?.Path
        ?? ComponentIconAttribute.DefaultPath;
}
