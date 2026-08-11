namespace Crowbar.Engine;

/// <summary>
/// Declares which viewport gizmo icon a component displays. The value is the
/// icon name — the SVG file in <c>Assets/Gizmos/&lt;name&gt;.svg</c> — so a new
/// icon only needs its file and this attribute, with no renderer change.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GizmoIconAttribute : Attribute
{
    public GizmoIconAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The icon name matching <c>Assets/Gizmos/&lt;name&gt;.svg</c>.</summary>
    public string Name { get; }
}
