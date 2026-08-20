using Crowbar.Engine.Platform;
using Crowbar.Engine.Rendering;
using Crowbar.UI;

namespace Crowbar.Engine.Global;

/// <summary>
/// The shared live-engine API — the s&amp;box-style <c>Game</c>, exposed
/// globally as <see cref="GlobalNamespaces.Game"/>. Exposes the live session
/// state of the primary window: the world every window renders and the primary
/// window's renderer, camera, Razor UI and platform window. It is bound to the
/// running <see cref="Application"/> in the application constructor and unbound
/// on disposal. It keeps no reference to the editor: the open level's
/// load/save/undo lifecycle is an editor tool, not part of this surface — a
/// game project references the engine and reads exactly this API, never the
/// editor.
/// </summary>
public sealed class Game
{
    private Application? _host;

    internal void Bind(Application host) => _host = host;

    internal void Unbind() => _host = null;

    /// <summary>The world every window renders (assigned by the host).</summary>
    public World World => _host!.World!;

    /// <summary>The primary window's renderer (scene + Razor composite), or null headless.</summary>
    public Renderer? Renderer => _host!.Renderer;

    /// <summary>The primary window's viewport camera.</summary>
    public Camera Camera => _host!.Camera;

    /// <summary>The primary window's Razor UI runtime.</summary>
    public UiSystem Ui => _host!.Ui;

    /// <summary>The primary window.</summary>
    public IWindow Window => _host!.Window;
}
