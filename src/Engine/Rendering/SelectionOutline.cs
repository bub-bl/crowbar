using System.Numerics;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Editor-only contour drawn around the selected entity in the viewport. A
/// real post-process outline: the selected mesh renders into a mask texture,
/// then a fullscreen pass dilates that mask and tints the silhouette's edge —
/// not an inverted-hull geometry hack, so it stays correct at silhouettes and
/// thin features. Disabled automatically in game (the viewport gizmos are the
/// only editor overlay).
/// </summary>
public sealed class SelectionOutline
{
    /// <summary>Whether the contour is drawn while an entity is selected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Outline color (linear space; orange, the Unreal selection color).</summary>
    public Vector3 Color { get; set; } = new(1f, 0.62f, 0.12f);

    /// <summary>Opacity of the outline band, 0..1.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Outline radius in pixels; the band has a soft falloff on both edges.</summary>
    public float Thickness { get; set; } = 4f;
}
