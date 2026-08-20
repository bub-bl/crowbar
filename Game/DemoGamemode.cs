// Demo gamemode: a .NET 11 class library (Game/Game.csproj) referencing the
// engine. The editor's ScriptHost loads it at runtime into its own collectible
// assembly context (the engine never references this project) and hot-reloads
// it from the Game/ folder. Edit this file while the editor is running:
//   - changing a method body → IL hot reload (fast path), the instance keeps
//     its identity and its state;
//   - adding/removing a field or method → full reload, state is migrated.
// The status bar shows Describe() live and a notification appears on every
// reload.

namespace Game;

/// <summary>State of the demo gamemode; fields survive reloads.</summary>
public sealed class DemoGamemode
{
    public static int ReloadCount;

    public int Score = 5;
    public string? Name = "Démo";

    /// <summary>Status line shown in the editor's status bar.</summary>
    public string Describe() => $"Gamemode {Name} : {Score} points, {ReloadCount} recharges";

    public void Bump() => Score++;
}
