// Demo game project: a .NET 11 class library (Game/Game.csproj) referencing the
// engine. It's a real game project, like Unity or Unreal: it ships Components
// the editor can attach to entities, and it's never referenced by the editor.
// The editor's ScriptHost loads it at runtime into its own collectible assembly
// context and hot-reloads it from the Game/ folder. Edit this file while the
// editor is running:
//   - changing a method body → IL hot reload (fast path), instances keep their
//     identity and their state;
//   - adding/removing a field or method → full reload, state is migrated.
// Components publish editor status through the engine's Editor.StatusBar API
// (the editor never reflects on game types) and a notification appears on every
// reload.

using Crowbar.Editor;
using Crowbar.Engine;

namespace Game;

/// <summary>
/// A demo component: attach it to an entity from the editor's inspector (Add
/// component) and it publishes a live status bar entry. Its properties are
/// editable in the inspector and survive hot reloads.
/// </summary>
public sealed class DemoComponent : Component
{
    public static int ReloadCount;

    [Property]
    public int Score { get; set; } = 5;

    [Property]
    public string? Name { get; set; } = "Demo";

    /// <summary>
    /// Publishes the component's status bar entry. OnInitialize runs when the
    /// component is attached to an entity (from the editor or from a saved
    /// level); OnDestroy removes the entry when the component (or its entity)
    /// is destroyed.
    /// </summary>
    protected override void OnInitialize()
    {
        StatusBar.AddEntry("Game", () => $"{Name} — {Score} points, {ReloadCount} reloads");
    }

    protected override void OnDestroy()
    {
        StatusBar.RemoveEntry("Game");
    }

    public void Bump() => Score++;
}
