using Crowbar.Engine;
using Crowbar.Engine.Audio;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Context-menu actions the editor contributes to content files, declared the
/// same way game code declares them — each [AssetAction] static method is one
/// action, discovered when the editor assembly is registered with the action
/// registry. Reimport reloads a cached asset from disk so the next load
/// re-reads it, and re-resolves the scene renderers bound to a reloaded model.
/// </summary>
public static class ContentActions
{
    /// <summary>Re-imports the file's cached resource: discard, reload bound scene models, report.</summary>
    [AssetAction("reimport", "Reimport", AssetType = typeof(Model))]
    [AssetAction("reimport", "Reimport", AssetType = typeof(Texture2D))]
    [AssetAction("reimport", "Reimport", AssetType = typeof(AudioClip))]
    [AssetAction("reimport", "Reimport", AssetType = typeof(Shader))]
    public static void Reimport(AssetActionContext ctx)
    {
        if (!FileSystem.Project.FileExists(ctx.Path))
        {
            ctx.Notify?.Invoke("Content", $"File not found: {ctx.Path}", "error");
            return;
        }

        var reloaded = new HashSet<string>(StringComparer.Ordinal);
        if (ResourceLibrary.Invalidate(ctx.Path))
            reloaded.Add(ctx.Path);
        if (reloaded.Count > 0)
            ContentExplorer.ReloadSceneModels(reloaded);
        ctx.Notify?.Invoke("Content", $"Reimported {Path.GetFileName(ctx.Path)}", "success");
    }
}
