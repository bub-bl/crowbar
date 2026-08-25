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

    // In-progress edit buffer per input. While a field is focused, the editor
    // shows exactly what the user typed instead of the canonical value; the
    // host's per-frame snapshot (which re-canonicalizes the parsed number, e.g.
    // turning a cleared "0.45" back into "0.45") would otherwise stomp the text
    // the instant the value is committed. Edits are committed to the entity on
    // blur/Enter, never on every keystroke.
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _editing = new(StringComparer.Ordinal);

    /// <summary>The text an input with the given token should render: its in-progress draft while focused, else the canonical value.</summary>
    protected string DraftValue(string token, string canonical) =>
        _editing.Contains(token) ? _drafts[token] : canonical;

    /// <summary>Called on focus: snapshots the canonical value as the edit's starting draft.</summary>
    protected void BeginEdit(string token, string canonical)
    {
        if (!_editing.Contains(token))
        {
            _editing.Add(token);
            _drafts[token] = canonical;
        }
    }

    /// <summary>Updates the in-progress draft on input (does not touch the entity).</summary>
    protected void UpdateDraft(string token, string text)
    {
        if (_editing.Contains(token))
            _drafts[token] = text;
    }

    /// <summary>Ends an edit session, forgetting the draft. Returns the draft text.</summary>
    protected string EndEdit(string token)
    {
        var text = _editing.Contains(token) ? _drafts.GetValueOrDefault(token, string.Empty) : string.Empty;
        _editing.Remove(token);
        _drafts.Remove(token);
        return text;
    }

    /// <summary>Whether a focused draft exists for the token (used to keep the guard cell stable across rebuilds).</summary>
    protected bool IsEditing(string token) => _editing.Contains(token);
}
