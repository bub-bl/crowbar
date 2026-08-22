namespace Crowbar.Engine;

/// <summary>
/// Declares one context-menu action on a static method. The action appears on
/// every file whose extension matches — <see cref="Extensions"/>, or the
/// <see cref="AssetTypeAttribute"/> extensions of <see cref="AssetType"/> —
/// or on every file when no filter is given. The method must be static, return
/// void and take a single <see cref="AssetActionContext"/>; the engine compiles
/// it into the action's handler when its assembly is registered with
/// <see cref="Global.AssetActions.Register"/>. Several attributes may decorate
/// the same method (one per targeted type, e.g. Reimport for Model and
/// Texture2D): their filters merge into one action.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AssetActionAttribute(string id, string label) : Attribute
{
    /// <summary>Stable action id the host dispatches on (unique across registered assemblies).</summary>
    public string Id { get; } = id;

    /// <summary>The menu label.</summary>
    public string Label { get; } = label;

    /// <summary>True when the menu item renders with the destructive styling.</summary>
    public bool IsDanger { get; init; }

    /// <summary>Sorting key among the file's actions (lower first).</summary>
    public int Order { get; init; }

    /// <summary>File extensions the action applies to (without the leading dot); empty means every file.</summary>
    public string[]? Extensions { get; init; }

    /// <summary>Registered asset type whose <see cref="AssetTypeAttribute"/> extensions the action applies to.</summary>
    public Type? AssetType { get; init; }
}

/// <summary>
/// The context an asset action handler receives when the user picks the menu
/// item: the file's logical content path, and a notification callback wired by
/// the host (title, message, kind) so handlers surface results without
/// touching editor types.
/// </summary>
public sealed class AssetActionContext(string path, Action<string, string, string>? notify = null)
{
    /// <summary>The file's logical content path (Content/Models/Crate/Crate.gltf).</summary>
    public string Path { get; } = path;

    /// <summary>Shows a notification (title, message, kind) when the host wired one.</summary>
    public Action<string, string, string>? Notify { get; } = notify;
}
