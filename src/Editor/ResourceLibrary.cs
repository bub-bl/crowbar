using Crowbar.Engine;

namespace Crowbar.Editor;

/// <summary>
/// The editor's resource loading API — the s&amp;box-style <c>ResourceLibrary</c>.
/// Centralizes loading with the engine's failure fallbacks (an unreadable model
/// becomes <see cref="Model.Error"/> instead of throwing), so content code
/// (e.g. <see cref="DemoScene"/>) never repeats the try/catch dance.
/// </summary>
public static class ResourceLibrary
{
    /// <summary>Loads a model, or <see cref="Model.Error"/> when it cannot be loaded.</summary>
    public static Model LoadModel(string path)
    {
        try
        {
            return Model.Load(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Resource] Failed to load model '{path}': {ex.Message}");
            return Model.Error;
        }
    }
}
