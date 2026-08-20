namespace Crowbar.Engine.Global;

/// <summary>
/// The resource loading API — the s&amp;box-style <c>ResourceLibrary</c>.
/// Exposed globally as <see cref="GlobalNamespaces.ResourceLibrary"/>.
/// Centralizes loading with the engine's failure fallbacks (an unreadable
/// model becomes <see cref="Model.Error"/> instead of throwing), so content
/// code never repeats the try/catch dance.
/// </summary>
public sealed class ResourceLibrary
{
    /// <summary>Loads a model, or <see cref="Model.Error"/> when it cannot be loaded.</summary>
    public Model LoadModel(string path)
    {
        try
        {
            return Model.Load(path);
        }
        catch (Exception ex)
        {
            GlobalNamespaces.Log.Warn($"[Resource] Failed to load model '{path}': {ex.Message}");
            return Model.Error;
        }
    }
}
