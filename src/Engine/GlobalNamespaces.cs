namespace Crowbar.Engine;

/// <summary>
/// The shared API root — the s&amp;box-style global surface every part of the
/// runtime can reach: <see cref="Log"/>, <see cref="TypeLibrary"/>,
/// <see cref="ResourceLibrary"/> and <see cref="Game"/>. This is the API the
/// engine exposes to both the editor and the game project — a game project
/// references the engine and gets these, never the editor. Call sites reach
/// them through the shorthand imported by
/// <c>global using static Crowbar.Engine.GlobalNamespaces</c> (GlobalUsings.cs):
/// <c>Log.Info(...)</c>, <c>Game.World</c>, ... The concrete types live in the
/// <c>Crowbar.Engine.Global</c> namespace so their names never shadow these
/// accessor properties here (a type in scope always beats a using-static member,
/// so the types must stay out of the call sites' scope).
/// </summary>
public static class GlobalNamespaces
{
    /// <summary>The logging API (the console equivalent).</summary>
    public static Global.Log Log { get; } = new();

    /// <summary>The type registry: component types resolved by name.</summary>
    public static Global.TypeLibrary TypeLibrary { get; } = new();

    /// <summary>The resource loading API.</summary>
    public static Global.ResourceLibrary ResourceLibrary { get; } = new();

    /// <summary>
    /// The shared live-engine API: the world every window renders and the
    /// primary window's session state. Bound to the running
    /// <see cref="Application"/> at startup.
    /// </summary>
    public static Global.Game Game { get; } = new();
}
