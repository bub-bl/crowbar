using Crowbar.Engine;
using Crowbar.UI;
using Crowbar.FileSystems;
using System.Globalization;
using System.Numerics;

namespace Crowbar.Editor;

/// <summary>
/// Host-side bridge for the inspector panel: consumes UI requests (property
/// edits, add-component) and applies them to the selected entity inside undo
/// windows, then republishes the inspector's available-component list and the
/// panel itself. Runs every frame, separate from the <see cref="Viewport"/>
/// (which handles rendering, gizmo interaction and scene picking).
/// </summary>
public sealed class InspectorBridge
{
    private readonly Editor _editor;

    public InspectorBridge(Editor editor) => _editor = editor;

    /// <summary>
    /// Consumes and applies any pending inspector requests on
    /// <paramref name="selected"/> and republishes the inspector panel.
    /// </summary>
    public void Update(Entity? selected)
    {
        UpdateEnvironment(selected);
        // Property edits queued by the inspector (UI → host): applied inside one
        // undo window (a single field commit = one undoable step). A successful
        // edit marks the level dirty through Level.MarkDirty (inside ApplyEdit).
        var pendingEdits = EditorInspectorState.ConsumeEdits();
        if (selected is not null && pendingEdits.Count > 0)
        {
            using var step = _editor.Level.Step("Edit a property");
            foreach (var (key, value) in pendingEdits)
                InspectorStateBuilder.ApplyEdit(selected, key, value);
        }

        // Add Component requests: applied inside one undoable step, then the
        // available-component list is republished so the menu reflects the
        // updated entity.
        var addRequests = EditorInspectorState.ConsumeAddComponentRequests();
        if (selected is not null && addRequests.Count > 0)
        {
            using var step = _editor.Level.Step("Add a component");
            foreach (var typeName in addRequests)
                AddComponent(selected, typeName);
        }
        EditorInspectorState.PublishAvailableComponents(AttachableTo(selected));

        InspectorStateBuilder.Publish(selected);
    }

    private void UpdateEnvironment(Entity? selected)
    {
        var level = _editor.Level.Level;
        if (level is null)
            return;

        if (selected is null)
        {
            var edits = EditorEnvironmentState.ConsumeEdits();
            if (edits.Count > 0)
            {
                using var step = _editor.Level.Step("Edit the environment");
                foreach (var edit in edits)
                    ApplyEnvironmentEdit(level.Environment, edit);
            }

        }
        else
        {
            EditorEnvironmentState.ConsumeEdits();
        }

        if (EditorEnvironmentState.ConsumeSourcePickerRequest())
            ImportEnvironment(level);

        var environment = level.Environment;
        EditorEnvironmentState.Publish(new EditorEnvironmentState.Snapshot(
            environment.Sky?.Kind.ToString() ?? nameof(SkyProviderKind.None),
            (environment.Sky as CubemapSky)?.SourcePath ?? string.Empty,
            environment.Rotation,
            environment.Intensity,
            environment.Exposure,
            environment.Tint,
            environment.State.ToString(),
            environment.Diagnostic ?? string.Empty));
    }

    private static void ApplyEnvironmentEdit(SceneEnvironment environment, EditorEnvironmentState.Edit edit)
    {
        switch (edit.Property)
        {
            case "Provider":
                environment.Sky = edit.Value switch
                {
                    nameof(SkyProviderKind.ProceduralAtmosphere) => new ProceduralAtmosphere(),
                    nameof(SkyProviderKind.Cubemap) => new CubemapSky((environment.Sky as CubemapSky)?.SourcePath ?? string.Empty),
                    _ => null
                };
                break;
            case "Rotation" when TryFloat(edit.Value, out var rotation):
                environment.Rotation = rotation * MathF.PI / 180f;
                break;
            case "Intensity" when TryFloat(edit.Value, out var intensity):
                environment.Intensity = intensity;
                break;
            case "Exposure" when TryFloat(edit.Value, out var exposure):
                environment.Exposure = exposure;
                break;
            case "Tint" when TryVector4(edit.Value, out var tint):
                environment.Tint = tint;
                break;
        }
    }

    private void ImportEnvironment(Level level)
    {
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
            level.Environment.SetCubemap(destination);
            UiNotifications.Show("Environment", $"Imported {Path.GetFileName(source)}", "success");
        }
        catch (Exception ex)
        {
            UiNotifications.Show("Environment", $"Import failed: {ex.Message}", "error");
        }
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryVector4(string text, out Vector4 value)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 4 && parts.All(part => TryFloat(part, out _)))
        {
            value = new Vector4(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                float.Parse(parts[3], CultureInfo.InvariantCulture));
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// The component types the entity can still attach: every type registered in
    /// <see cref="GlobalNamespaces.TypeLibrary"/> minus the ones already on it,
    /// ordered by name. The inspector's Add Component menu offers them.
    /// </summary>
    private static IReadOnlyList<string> AttachableTo(Entity? entity)
    {
        if (entity is null)
            return [];
        return TypeLibrary.All
            .Where(type => entity.GetComponent(type) is null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToArray();
    }

    /// <summary>
    /// Attaches a component by name to the entity. A malformed name, an
    /// already-present type or a throwing constructor is ignored (with a
    /// warning).
    /// </summary>
    private void AddComponent(Entity? entity, string typeName)
    {
        if (entity is null || string.IsNullOrEmpty(typeName))
            return;
        var type = TypeLibrary.Resolve(typeName);
        if (type is null || entity.GetComponent(type) is not null)
            return;
        try
        {
            if (Activator.CreateInstance(type) is Component component)
                entity.AddComponent(component);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Inspector] Failed to add component '{typeName}': {ex.Message}");
        }
    }
}
