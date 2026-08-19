namespace Crowbar.Engine;

/// <summary>
/// Declares the editor icon a component displays in the hierarchy (Explorer)
/// and in its inspector section. The value is a Solar icon path
/// (<c>Solar/&lt;category&gt;/Bold/&lt;name&gt;</c>) matching an SVG under
/// <c>Assets/Icons/Solar</c>, so a new icon only needs its asset and this
/// attribute, with no builder change. Inherited, so a base class (e.g.
/// <see cref="Light"/>) can declare one icon for all its subclasses.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ComponentIconAttribute : Attribute
{
    /// <summary>The fallback icon when a component declares none.</summary>
    public const string DefaultPath = "Solar/ui/Bold/box";

    public ComponentIconAttribute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>The Solar icon path (<c>Solar/&lt;category&gt;/Bold/&lt;name&gt;</c>).</summary>
    public string Path { get; }
}
