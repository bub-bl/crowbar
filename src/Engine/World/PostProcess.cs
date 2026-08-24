using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Base class for fullscreen post-process effects. Each derived component
/// names one shader from Shaders/PostProcesses/; the renderer chains the
/// enabled instances by <see cref="Order"/> between the linear HDR scene and
/// the display texture. With no instance the engine keeps its default display
/// transform.
/// </summary>
public abstract class PostProcess : Component
{
    /// <summary>Execution order in the chain; lower values run first.</summary>
    [Property]
    public int Order { get; set; }

    /// <summary>Project-relative path of this effect's fullscreen shader.</summary>
    internal abstract string ShaderPath { get; }

    /// <summary>Value written to the shader's float4 "settings" uniform.</summary>
    internal abstract Vector4 Settings { get; }
}
