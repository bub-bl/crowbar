using Crowbar.Engine;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// Handles the editor's environment asset picker independently from the generic
/// inspector request bridge. The target component and its source property are
/// discovered through reflection, keeping this workflow compatible with the
/// component hierarchy and game-provided types.
/// </summary>
internal sealed class EnvironmentAssetImporter
{
    private readonly Editor _editor;

    public EnvironmentAssetImporter(Editor editor) => _editor = editor;

    /// <summary>Consumes a pending environment picker request and applies its result.</summary>
    public void Update(Entity? selected)
    {
        if (!EditorEnvironmentState.ConsumeSourcePickerRequest() || selected is null)
            return;

        var component = selected.Components.Cast<object>().FirstOrDefault(IsImportTarget);
        if (component is null)
            return;

        var sourceProperty = component.GetType().GetProperty("SourcePath");
        if (sourceProperty?.CanWrite != true || sourceProperty.PropertyType != typeof(string))
            return;

        var source = NativeFileDialog.PickEnvironment(
            _editor.Window.NativeHandle,
            _editor.Project.FilePath is { } projectFile ? Path.GetDirectoryName(projectFile) : null);
        if (source is null)
            return;

        try
        {
            const string folder = "Content/Environments";
            FileSystem.Project.CreateDirectory(folder);
            var destination = $"{folder}/{Path.GetFileName(source)}";
            FileSystem.Project.WriteAllBytes(destination, File.ReadAllBytes(source));
            Texture2D.Invalidate(destination);
            _editor.ContentExplorer.Refresh();
            using var step = _editor.Level.Step("Import an HDR environment");
            sourceProperty.SetValue(component, destination);
            UiNotifications.Show("Environment", $"Imported {Path.GetFileName(source)}", "success");
        }
        catch (Exception ex)
        {
            UiNotifications.Show("Environment", $"Import failed: {ex.Message}", "error");
        }
    }

    private static bool IsImportTarget(object component)
    {
        var type = component.GetType();
        var sourceProperty = type.GetProperty("SourcePath");
        return sourceProperty?.CanWrite == true && sourceProperty.PropertyType == typeof(string);
    }

}
