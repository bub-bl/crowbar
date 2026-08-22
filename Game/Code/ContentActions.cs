// Demo of the engine's context-action API: any [AssetAction] static method in
// the game project becomes a context-menu item in the editor's Content panel
// (here: on every .level file), without touching the editor. The action id is
// what the host dispatches on; the handler receives the file's logical content
// path and a notification callback wired to the editor.

using Crowbar.Engine;

namespace Game;

public static class ContentActions
{
    [AssetAction("game.open-level", "Open in Game", Extensions = ["level"])]
    public static void OpenLevel(AssetActionContext ctx)
    {
        ctx.Notify?.Invoke("Game", $"Opening {ctx.Path}...", "info");
    }
}
