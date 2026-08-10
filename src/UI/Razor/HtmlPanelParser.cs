using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Crowbar.UI;

/// <summary>
/// Parses the markup produced by a component's <c>ExecuteAsync</c> into a
/// <see cref="Panel"/> tree, resolving child components, fragment markers,
/// synthetic event attributes and preserved input state.
/// </summary>
internal static class HtmlPanelParser
{
    public static PanelComponent Parse(string markup, RazorPanel root,
        IReadOnlyDictionary<string, Func<RazorPanel>>? components = null)
    {
        root.TagName = "root";
        if (!string.IsNullOrEmpty(root.ScopeId)) root.AddScope(root.ScopeId);
        var previousTree = SnapshotTree(root);
        root.ClearChildren();
        if (string.IsNullOrWhiteSpace(markup)) return root;
        try
        {
            var xml = XDocument.Parse("<root>" + markup + "</root>", LoadOptions.PreserveWhitespace);
            var index = 0;
            foreach (var node in xml.Root!.Nodes())
            {
                // Keys mirror panel positions so that preserved inputs, child
                // components and animation state line up across renders.
                // Whitespace-only text nodes produce no panel, so they must not
                // consume an index.
                if (node is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
                AddNode(root, node, root, components, $"root/{index}", previousTree);
                index++;
            }

            return root;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Razor rendered invalid UI markup: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Snapshots the current panel tree keyed by its positional paths — the
    /// same keys <see cref="AddNode"/> assigns while rebuilding. A re-render
    /// uses the snapshot to restore preserved input state and to hand the
    /// running CSS animation/transition clocks over to the fresh panels, so a
    /// re-render does not restart them.
    /// </summary>
    private static Dictionary<string, Panel> SnapshotTree(Panel root)
    {
        var result = new Dictionary<string, Panel>(StringComparer.Ordinal);
        Visit(root, "root", result);
        return result;

        static void Visit(Panel panel, string key, Dictionary<string, Panel> result)
        {
            result[key] = panel;
            for (var i = 0; i < panel.Children.Count; i++) Visit(panel.Children[i], $"{key}/{i}", result);
        }
    }

    /// <summary>
    /// Hands the running CSS animation/transition clocks of the previous panel
    /// at <paramref name="key"/> (when it is the same kind of element) over to
    /// <paramref name="fresh"/>. Text nodes and elements both participate; the
    /// reference check guards fragment panels that are reused across renders
    /// rather than rebuilt.
    /// </summary>
    private static void TransferAnimationState(IReadOnlyDictionary<string, Panel>? previousTree, string key, Panel fresh)
    {
        if (previousTree is null || !previousTree.TryGetValue(key, out var previous) || ReferenceEquals(previous, fresh)) return;
        if (previous.GetType() != fresh.GetType()) return;
        if (!string.Equals(previous.TagName, fresh.TagName, StringComparison.OrdinalIgnoreCase)) return;
        fresh.CarryOverAnimationState(previous);
    }

    private static void AddNode(Panel parent, XNode node, RazorPanel runtime,
        IReadOnlyDictionary<string, Func<RazorPanel>>? components, string key,
        IReadOnlyDictionary<string, Panel>? previousTree)
    {
        if (node is XText text)
        {
            if (FragmentMarkerRegex.IsMatch(text.Value))
            {
                SpliceChildContent(parent, text.Value, runtime, key, previousTree);
                return;
            }

            if (!string.IsNullOrWhiteSpace(text.Value))
            {
                var textPanel = new Panel { TagName = "text", Text = text.Value };
                if (!string.IsNullOrEmpty(runtime.ScopeId)) textPanel.AddScope(runtime.ScopeId);
                TransferAnimationState(previousTree, key, textPanel);
                parent.AddChild(textPanel);
            }

            return;
        }

        if (node is not XElement element) return;
        if (components is not null && components.TryGetValue(element.Name.LocalName, out var componentFactory))
        {
            var child = runtime.GetOrCreateChild(key, element.Name.LocalName, componentFactory);
            child.StateChanged = runtime.StateHasChanged;
            child.NavigationRequested = runtime.NavigationRequested;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase))
                    foreach (var value in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        child.AddClass(value);
                else if (attribute.Name.LocalName.StartsWith("data-codex-", StringComparison.OrdinalIgnoreCase))
                    continue; // Skip synthetic event attributes – they are handled only on HTML elements
                else child.SetParameter(attribute.Name.LocalName, attribute.Value);
            }

            // Capture the markup between the component's tags. It is parsed with
            // the parent as runtime: expressions were already evaluated by the
            // parent's ExecuteAsync and event/binding attributes refer to parent
            // members. Named region elements (<Header>, <Body>, ...) matching a
            // RenderFragment parameter of the component feed that fragment;
            // everything else feeds the default ChildContent fragment. The
            // panels are handed to the child so its @Fragment placeholder (a
            // marker text node) can be replaced by them.
            var providedFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regionNodes = new Dictionary<string, List<XNode>>(StringComparer.OrdinalIgnoreCase);
            var childContentNodes = new List<XNode>();
            foreach (var childNode in element.Nodes())
            {
                if (childNode is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
                if (childNode is XElement regionElement &&
                    child.HasRenderFragmentParameter(regionElement.Name.LocalName))
                {
                    var regionName = regionElement.Name.LocalName;
                    if (!regionNodes.TryGetValue(regionName, out var region))
                        regionNodes[regionName] = region = [];
                    foreach (var inner in regionElement.Nodes())
                        if (inner is not XText whitespaceOnly || !string.IsNullOrWhiteSpace(whitespaceOnly.Value))
                            region.Add(inner);
                    continue;
                }

                childContentNodes.Add(childNode);
            }

            foreach (var (regionName, nodes) in regionNodes)
            {
                providedFragments.Add(regionName);
                var signature = string.Concat(nodes.Select(node => node.ToString()));
                if (signature != child.GetFragmentSignature(regionName))
                    child.SetFragment(regionName, BuildFragmentPanels(nodes, key, regionName, runtime, components),
                        signature);
            }

            providedFragments.Add("ChildContent");
            var contentSignature = string.Concat(childContentNodes.Select(node => node.ToString()));
            if (contentSignature != child.GetFragmentSignature("ChildContent"))
                child.SetFragment("ChildContent", BuildFragmentPanels(childContentNodes, key, "ChildContent", runtime,
                    components), contentSignature);

            // Fragments the parent no longer provides (e.g. a region removed by
            // an @if) must be cleared so the child re-renders without them.
            foreach (var staleName in child.ProvidedFragmentNames.Where(name => !providedFragments.Contains(name)))
                child.SetFragment(staleName, null, string.Empty);

            var childTree = new RazorComponentFactory(components).BuildTree(child);
            // The child tree keeps only its own scope. Applying the parent's scope
            // to the child's root would leak parent scoped CSS (e.g. the page's
            // `root { height: ... }` rule) into every nested component root.
            parent.AddChild(childTree);
            return;
        }

        var panel = element.Name.LocalName.ToLowerInvariant() switch
        {
            "button" => new Button(),
            "input" => new TextInput(),
            "img" or "image" => new Image(),
            "label" or "span" => new Label(),
            _ => new Panel()
        };
        panel.TagName = element.Name.LocalName;
        if (!string.IsNullOrEmpty(runtime.ScopeId)) panel.AddScope(runtime.ScopeId);
        // The panel tree is rebuilt on every render: keep the running CSS
        // animations/transitions of the panel that was here before, so a
        // re-render does not restart them.
        TransferAnimationState(previousTree, key, panel);
        string? click = null, change = null, bind = null;
        string? declaredValue = null;
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name == "class")
                foreach (var c in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    panel.AddClass(c);
            else if (attribute.Name == "id") panel.Id = attribute.Value;
            else if (attribute.Name == "style")
                foreach (var declaration in attribute.Value.Split(';'))
                {
                    var p = declaration.Split(':', 2);
                    if (p.Length == 2) panel.SetInlineStyle(p[0].Trim(), p[1].Trim());
                }
            else if (attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase) && panel is TextInput)
                declaredValue = attribute.Value;
            else if (attribute.Name.LocalName.Equals("data-codex-onclick", StringComparison.OrdinalIgnoreCase))
                click = attribute.Value;
            else if (attribute.Name.LocalName.Equals("data-codex-onchange", StringComparison.OrdinalIgnoreCase))
                change = attribute.Value;
            else if (attribute.Name.LocalName.Equals("data-codex-bind-value", StringComparison.OrdinalIgnoreCase))
                bind = attribute.Value;
            else panel.Attributes[attribute.Name.LocalName] = attribute.Value;
        }

        var childIndex = 0;
        foreach (var child in element.Nodes())
        {
            if (child is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
            AddNode(panel, child, runtime, components, $"{key}/{childIndex}", previousTree);
            childIndex++;
        }

        if (panel is TextInput inputValue)
        {
            if (previousTree is not null && previousTree.TryGetValue(key, out var previous) && previous is TextInput preserved)
            {
                inputValue.SetValue(preserved.Value, preserved.CaretIndex);
                inputValue.CopyInteractionStateFrom(preserved);
            }
            else inputValue.SetValue(declaredValue ?? string.Empty);
        }

        if (panel is Button button && click is not null)
            button.Clicked += e => RazorEventInvoker.Invoke(runtime, click, e);
        if (panel is TextInput textInput)
        {
            if (change is not null) textInput.ValueChanged += value => RazorEventInvoker.Invoke(runtime, change, value);
            if (bind is not null) textInput.ValueChanged += value => RazorEventInvoker.SetValue(runtime, bind, value);
        }

        parent.AddChild(panel);
    }

    /// <summary>
    /// Matches any fragment marker in a rendered text node (default ChildContent
    /// or a named region). The name is captured up to the closing brackets so
    /// non-ASCII identifiers (e.g. <c>Tête</c>) parse correctly; the marker is
    /// self-delimiting so there is no ambiguity.
    /// </summary>
    private static readonly Regex FragmentMarkerRegex = new(
        @"\[\[__CROWBAR_(?:CHILDCONTENT__|FRAGMENT__:([^\]\[]+))\]\]",
        RegexOptions.Compiled);

    /// <summary>
    /// Replaces fragment marker text nodes with the captured fragment panels,
    /// preserving any surrounding text and restoring input state with keys
    /// relative to the current tree (fragment inputs were built fresh by the
    /// parent's capture pass, so they are restored here instead).
    /// </summary>
    private static void SpliceChildContent(Panel parent, string content, RazorPanel runtime, string key,
        IReadOnlyDictionary<string, Panel>? previousTree)
    {
        var lastSlash = key.LastIndexOf('/');
        var parentKey = lastSlash > 0 ? key[..lastSlash] : key;
        var baseIndex = lastSlash > 0 && int.TryParse(key[(lastSlash + 1)..], out var parsed) ? parsed : 0;
        var insertIndex = baseIndex;
        var position = 0;
        foreach (Match match in FragmentMarkerRegex.Matches(content))
        {
            if (match.Index > position)
                AddSpliceText(parent, content[position..match.Index], runtime, ref insertIndex);
            var fragmentName = match.Groups[1].Success ? match.Groups[1].Value : "ChildContent";
            var panels = runtime.GetFragmentPanels(fragmentName);
            if (panels is not null)
            {
                foreach (var panel in panels)
                {
                    var panelKey = $"{parentKey}/{insertIndex}";
                    RestorePreservedInputs(panel, panelKey, previousTree);
                    // A fragment whose markup changed was rebuilt with fresh
                    // panels: carry their animation clocks over from the panel
                    // that occupied the spot before. Unchanged fragments reuse
                    // the same instances, which the reference check skips.
                    TransferAnimationState(previousTree, panelKey, panel);
                    parent.AddChild(panel);
                    insertIndex++;
                }
            }

            position = match.Index + match.Length;
        }

        if (position < content.Length)
            AddSpliceText(parent, content[position..], runtime, ref insertIndex);
    }

    private static void AddSpliceText(Panel parent, string text, RazorPanel runtime, ref int insertIndex)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var textPanel = new Panel { TagName = "text", Text = text };
        if (!string.IsNullOrEmpty(runtime.ScopeId)) textPanel.AddScope(runtime.ScopeId);
        parent.AddChild(textPanel);
        insertIndex++;
    }

    private static List<Panel>? BuildFragmentPanels(List<XNode> nodes, string key, string name, RazorPanel runtime,
        IReadOnlyDictionary<string, Func<RazorPanel>>? components)
    {
        if (nodes.Count == 0) return null;
        var container = new Panel();
        // Fragment keys are local to the fragment capture and never match the
        // previous tree's positional paths; state is handed over at splice time
        // (see SpliceChildContent), so no snapshot is needed here.
        for (var i = 0; i < nodes.Count; i++)
            AddNode(container, nodes[i], runtime, components, $"{key}/fragment/{name}/{i}", previousTree: null);
        return [.. container.Children];
    }

    private static void RestorePreservedInputs(Panel panel, string key,
        IReadOnlyDictionary<string, Panel>? previousTree)
    {
        if (panel is TextInput input && previousTree is not null &&
            previousTree.TryGetValue(key, out var previous) && previous is TextInput previousInput)
        {
            input.SetValue(previousInput.Value, previousInput.CaretIndex);
            input.CopyInteractionStateFrom(previousInput);
        }

        for (var i = 0; i < panel.Children.Count; i++)
            RestorePreservedInputs(panel.Children[i], $"{key}/{i}", previousTree);
    }
}
