using System.Reflection;

namespace Crowbar.Engine;

/// <summary>
/// A single-pass post-process declared in C# only: the shader reads the
/// previous chain texture and writes the chain output once, and the shader's
/// group-0 uniform buffer fields are packed automatically from the component's
/// <see cref="PropertyAttribute"/> values by name. For anything more
/// (multi-pass, conditional work, spatial blending) derive from
/// <see cref="PostProcess"/> or <see cref="BasePostProcess{T}"/> and override
/// <see cref="PostProcess.Render"/>.
/// </summary>
public abstract class SinglePassPostProcess : PostProcess
{
    /// <summary>
    /// Project-relative path of this effect's fullscreen shader (for example
    /// <c>Shaders/PostProcesses/MyEffect.wgsl</c>, or
    /// <c>Content/Shaders/MyEffect.wgsl</c> for a game-shipped shader).
    /// </summary>
    public abstract string ShaderPath { get; }

    public sealed override void Render(PostProcessContext context)
    {
        context.Blit(context.Input, context.Output, ShaderPath, PostProcessAttributes.Collect(this));
    }
}

/// <summary>Reflective collection of a component's [Property] values into <see cref="RenderAttributes"/>.</summary>
internal static class PostProcessAttributes
{
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    /// <summary>
    /// Collects every writable [Property] value of <paramref name="component"/>
    /// (excluding the base-class infrastructure like <see cref="PostProcess.Order"/>)
    /// into a <see cref="RenderAttributes"/> bag, keyed by property name.
    /// </summary>
    public static RenderAttributes Collect(PostProcess component)
    {
        var attributes = new RenderAttributes();
        foreach (var property in component.GetType()
                     .GetProperties(InstancePublic)
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .Where(p => p.GetGetMethod() is not null)
                     .Where(p => p.IsDefined(typeof(PropertyAttribute), inherit: true))
                     .Where(p => !IsInfrastructure(p)))
        {
            var value = property.GetValue(component);
            if (value is null)
                continue;
            attributes.Set(property.Name, value);
        }

        return attributes;
    }

    private static bool IsInfrastructure(PropertyInfo property) =>
        property.DeclaringType == typeof(PostProcess) ||
        property.DeclaringType == typeof(Component) ||
        property.DeclaringType == typeof(WorldObject);
}
