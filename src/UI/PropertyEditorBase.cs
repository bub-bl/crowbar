using Microsoft.AspNetCore.Components;

namespace Crowbar.UI;

/// <summary>
/// Common parameter contract shared by every per-type property editor: the
/// label, the single serialized value, the nesting indent and the write-back
/// key. An editor parses <see cref="Value"/> as needed (a vector editor splits
/// it into its axes), so editors never carry fields they do not render. Lives
/// in Crowbar.UI so runtime-compiled editor components can inherit it in any
/// host (the editor app and the UI tests).
/// </summary>
public abstract class PropertyEditorBase : RazorPanel
{
    [Parameter]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public int Indent { get; set; }

    [Parameter]
    public string Key { get; set; } = string.Empty;
}
