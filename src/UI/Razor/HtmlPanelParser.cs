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
        IReadOnlyDictionary<string, RazorComponentSource>? components = null)
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
            for (var i = 0; i < panel.Children.Count; i++)
            {
                var child = panel.Children[i];
                // Keyed panels are snapshotted under their key identity (not
                // their position), mirroring AddNode's effective-key paths, so
                // a sibling insertion above them cannot steal their state.
                var childKey = child.Attributes.TryGetValue("data-codex-key", out var keyValue)
                    ? $"{key}/key:{keyValue}"
                    : $"{key}/{i}";
                Visit(child, childKey, result);
            }
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
        IReadOnlyDictionary<string, RazorComponentSource>? components, string key,
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
                // Text panels are hit targets like any other panel: carry the
                // reconciliation key so identity-based logic (double-click
                // detection) works on text the same way it does on elements.
                textPanel.Key = key;
                if (!string.IsNullOrEmpty(runtime.ScopeId)) textPanel.AddScope(runtime.ScopeId);
                TransferAnimationState(previousTree, key, textPanel);
                parent.AddChild(textPanel);
            }

            return;
        }

        if (node is not XElement element) return;
        if (components is not null && components.TryGetValue(element.Name.LocalName, out var componentSource))
        {
            // Attributes matching the component's @typeparam names are type
            // arguments (the closed generic is created by the source), not
            // parameters; the rest are parameters as usual.
            var typeParams = componentSource.TypeParameters;
            var typeArguments = typeParams.Length == 0
                ? null
                : element.Attributes()
                    .Where(a => typeParams.Contains(a.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);
            // Component instances are stateful (BuildHash, fragments and child
            // components) and must survive parent re-renders. Creating a fresh
            // instance here bypasses the reconciliation cache and, for file
            // components, recompiles/reopens the .razor file on every render.
            // @key gives the instance a stable identity across the tree: the
            // key value replaces the positional path, so a component that
            // moves to a different markup position (e.g. a docked panel
            // re-docked into another group) keeps its instance and state.
            var componentKeyAttribute = element.Attribute("data-codex-key");
            var componentKey = componentKeyAttribute is not null ? componentKeyAttribute.Value : key;
            var child = runtime.GetOrCreateChild(componentKey, element.Name.LocalName,
                () => componentSource.Create(typeArguments));
            if (element.Attribute("data-codex-ref") is { } refAttribute)
                runtime.AddRef(CleanRefName(refAttribute.Value), child);
            // Child state changes must invalidate the owning render scope so
            // parent content and keyed siblings are reconciled correctly. The
            // high-frequency DockArea pointer handler opts out explicitly by
            // returning false from its event method.
            child.StateChanged = runtime.StateHasChanged;
            child.NavigationRequested = runtime.NavigationRequested;
            child.ViewportPublishRequested = runtime.ViewportPublishRequested;
            child.WindowChromePublishRequested = runtime.WindowChromePublishRequested;
            child.WindowChromeStateProvider = runtime.WindowChromeStateProvider;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase))
                    foreach (var value in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        child.AddClass(value);
                else if (attribute.Name.LocalName.StartsWith("data-codex-", StringComparison.OrdinalIgnoreCase))
                    continue; // Skip synthetic event attributes – they are handled only on HTML elements
                else if (typeParams.Contains(attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                    continue; // Type argument, not a parameter.
                else if (attribute.Name.LocalName.Equals("tooltip", StringComparison.OrdinalIgnoreCase))
                    child.Tooltip = attribute.Value; // Hover overlay, not a [Parameter].
                else child.SetParameter(attribute.Name.LocalName, attribute.Value, runtime);
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
                else
                    ReactivateFragmentComponents(runtime, child.GetFragmentPanels(regionName), components);
            }

            providedFragments.Add("ChildContent");
            var contentSignature = string.Concat(childContentNodes.Select(node => node.ToString()));
            if (contentSignature != child.GetFragmentSignature("ChildContent"))
                child.SetFragment("ChildContent", BuildFragmentPanels(childContentNodes, key, "ChildContent", runtime,
                    components), contentSignature);
            else
                ReactivateFragmentComponents(runtime, child.GetFragmentPanels("ChildContent"), components);

            // Fragments the parent no longer provides (e.g. a region removed by
            // an @if) must be cleared so the child re-renders without them.
            foreach (var staleName in child.ProvidedFragmentNames.Where(name => !providedFragments.Contains(name)))
                child.SetFragment(staleName, null, string.Empty);

            var childTree = new RazorComponentFactory(components).BuildTree(child);
            // The child tree keeps only its own scope. Applying the parent's scope
            // to the child's root would leak parent scoped CSS (e.g. the page's
            // `root { height: ... }` rule) into every nested component root.
            parent.AddChild(childTree);
            child.ReplaceRenderedTree(childTree);
            return;
        }

        var inputType = element.Attribute("type")?.Value.Trim().ToLowerInvariant();
        var panel = element.Name.LocalName.ToLowerInvariant() switch
        {
            "button" => new Button(),
            "input" when inputType is "checkbox" => new ToggleInput(),
            "input" when inputType is "radio" => new ToggleInput(radio: true),
            "input" => new TextInput(),
            "img" or "image" => new Image(),
            "icon" => new Icon(),
            "label" or "span" => new Label(),
            _ => new Panel()
        };
        panel.TagName = element.Name.LocalName;
        if (!string.IsNullOrEmpty(runtime.ScopeId)) panel.AddScope(runtime.ScopeId);
        // @key="expr" gives the element a stable identity: its state (input
        // values, animation clocks) is transferred by the key value instead of
        // the positional index, so inserting a sibling above it no longer
        // steals its state. The attribute stays on the panel so the snapshot
        // can see it and selectors keep matching.
        var keyAttribute = element.Attribute("data-codex-key");
        var effectiveKey = keyAttribute is not null ? $"{key}/key:{keyAttribute.Value}" : key;
        // The key is the panel's stable identity across re-renders (the tree is
        // rebuilt on every render, so instance references are not stable).
        panel.Key = effectiveKey;
        if (element.Attribute("data-codex-ref") is { } elementRef)
            runtime.AddRef(CleanRefName(elementRef.Value), panel);
        // The panel tree is rebuilt on every render: keep the running CSS
        // animations/transitions of the panel that was here before, so a
        // re-render does not restart them.
        TransferAnimationState(previousTree, effectiveKey, panel);
        string? click = null, change = null, bind = null;
        string? declaredValue = null;
        var handlers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // @attributes="dict" splats a runtime dictionary onto the element;
        // explicit attributes processed by the loop below override splatted
        // values (last one wins, mirroring the source order).
        if (element.Attribute("data-codex-attributes") is { } splatAttribute)
        {
            foreach (var (splatName, splatValue) in runtime.ResolveAttributes(splatAttribute.Value) ?? [])
                ApplySplattedAttribute(panel, splatName, splatValue?.ToString() ?? string.Empty);
        }
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
            else if (attribute.Name.LocalName.Equals("placeholder", StringComparison.OrdinalIgnoreCase) && panel is TextInput placeholderInput)
            {
                placeholderInput.Placeholder = attribute.Value;
                panel.Attributes[attribute.Name.LocalName] = attribute.Value;
            }
            else if (attribute.Name.LocalName.Equals("src", StringComparison.OrdinalIgnoreCase) && panel is Image image)
            {
                // The image source feeds the renderer (object-fit) and the
                // layout measure (intrinsic size); the attribute stays in the
                // panel so attribute selectors like img[src=...] keep matching.
                image.Source = attribute.Value;
                panel.Attributes["src"] = attribute.Value;
            }
            else if (attribute.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) && panel is Icon icon)
                icon.Name = attribute.Value; // Icon name -> Assets/Icons/<name>.svg
            else if (attribute.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) && panel is ToggleInput toggle)
                toggle.GroupName = attribute.Value;
            else if (attribute.Name.LocalName.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                panel.IsEnabled = false;
            else if (attribute.Name.LocalName.Equals("checked", StringComparison.OrdinalIgnoreCase) && panel is ToggleInput)
                panel.IsChecked = IsTruthyAttribute(attribute.Value);
            else if (attribute.Name.LocalName.Equals("tooltip", StringComparison.OrdinalIgnoreCase))
                panel.Tooltip = attribute.Value; // Hover overlay; kept out of the generic attribute map.
            else if (attribute.Name.LocalName.StartsWith("data-codex-on", StringComparison.OrdinalIgnoreCase))
                handlers[attribute.Name.LocalName["data-codex-on".Length..]] = attribute.Value;
            else if (attribute.Name.LocalName.Equals("data-codex-bind-value", StringComparison.OrdinalIgnoreCase))
                bind = attribute.Value;
            else panel.Attributes[attribute.Name.LocalName] = attribute.Value;
        }
        click = handlers.GetValueOrDefault("click");
        change = handlers.GetValueOrDefault("change") ?? handlers.GetValueOrDefault("input");

        var childIndex = 0;
        foreach (var child in element.Nodes())
        {
            if (child is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
            // A keyed child keeps its identity path (`<parent>/key:<value>`)
            // instead of a positional index, so inserting or removing siblings
            // above it does not shift its state transfer key. The value is
            // appended by AddNode itself.
            var keyedChild = child is XElement childElement && childElement.Attribute("data-codex-key") is not null;
            AddNode(panel, child, runtime, components,
                keyedChild ? effectiveKey : $"{effectiveKey}/{childIndex}", previousTree);
            childIndex++;
        }

        if (panel is TextInput inputValue)
        {
            var previous = previousTree is not null &&
                           previousTree.TryGetValue(effectiveKey, out var existing) &&
                           existing is TextInput preserved
                ? preserved
                : null;
            ApplyTextInputState(inputValue, previous, declaredValue ?? string.Empty);
        }

        // Event handlers: @onclick / @onkeydown / @onmousemove / @onwheel / ...
        // are wired onto the panel's events (the @on* source attributes were
        // rewritten to data-codex-on* at compile time).
        if (click is not null)
            panel.Clicked += e => RazorEventInvoker.Invoke(runtime, click, e);
        if (handlers.TryGetValue("keydown", out var keyDown))
            panel.KeyDown += (_, e) => RazorEventInvoker.Invoke(runtime, keyDown, e);
        if (handlers.TryGetValue("keyup", out var keyUp))
            panel.KeyUp += (_, e) => RazorEventInvoker.Invoke(runtime, keyUp, e);
        if (handlers.TryGetValue("mousemove", out var mouseMove))
            panel.PointerMove += (_, e) => RazorEventInvoker.Invoke(runtime, mouseMove, e);
        if (handlers.TryGetValue("mousedown", out var mouseDown))
            panel.PointerDown += (_, e) => RazorEventInvoker.Invoke(runtime, mouseDown, e);
        if (handlers.TryGetValue("mouseup", out var mouseUp))
            panel.PointerUp += (_, e) => RazorEventInvoker.Invoke(runtime, mouseUp, e);
        if (handlers.TryGetValue("mouseenter", out var mouseEnter))
            panel.PointerEnter += _ => RazorEventInvoker.Invoke(runtime, mouseEnter, null);
        if (handlers.TryGetValue("mouseleave", out var mouseLeave))
            panel.PointerExit += _ => RazorEventInvoker.Invoke(runtime, mouseLeave, null);
        if (handlers.TryGetValue("wheel", out var wheel))
            panel.PointerWheel += (_, e) => RazorEventInvoker.Invoke(runtime, wheel, e);
        if (handlers.TryGetValue("dblclick", out var doubleClick))
            panel.DoubleClicked += (_, e) => RazorEventInvoker.Invoke(runtime, doubleClick, e);
        if (handlers.TryGetValue("focus", out var focus))
            panel.Focused += _ => RazorEventInvoker.Invoke(runtime, focus, null);
        if (handlers.TryGetValue("blur", out var blur))
            panel.Blurred += _ => RazorEventInvoker.Invoke(runtime, blur, null);
        if (handlers.TryGetValue("scroll", out var scroll))
            panel.Scrolled += _ => RazorEventInvoker.Invoke(runtime, scroll, null);
        if (panel is TextInput textInput)
        {
            if (change is not null) textInput.ValueChanged += value => RazorEventInvoker.Invoke(runtime, change, value);
            if (bind is not null) textInput.ValueChanged += value => RazorEventInvoker.SetValue(runtime, bind, value);
        }
        if (panel is ToggleInput toggleInput && handlers.TryGetValue("change", out var changeHandler))
            toggleInput.CheckedChanged += value => RazorEventInvoker.Invoke(runtime, changeHandler, value);

        // <a href="/..."> navigates through the router unless the author wired
        // an @onclick (which takes precedence). External URLs are left alone.
        if (panel.TagName.Equals("a", StringComparison.OrdinalIgnoreCase) && click is null &&
            panel.Attributes.TryGetValue("href", out var href) && href.StartsWith('/'))
        {
            var target = href;
            panel.Clicked += _ => runtime.NavigationRequested?.Invoke(target);
        }

        parent.AddChild(panel);
    }

    /// <summary>
    /// Matches any fragment marker in a rendered text node (default ChildContent
    /// or a named region). The name is captured up to the closing brackets so
    /// non-ASCII identifiers (e.g. <c>café</c>) parse correctly; the marker is
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
                AddSpliceText(parent, content[position..match.Index], runtime, $"{parentKey}/{insertIndex}", ref insertIndex);
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
            AddSpliceText(parent, content[position..], runtime, $"{parentKey}/{insertIndex}", ref insertIndex);
    }

    /// <summary>Applies one attribute value to a panel (used by @attributes splatting).</summary>
    private static void ApplySplattedAttribute(Panel panel, string name, string value)
    {
        if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) panel.AddClass(c);
        }
        else if (name.Equals("id", StringComparison.OrdinalIgnoreCase)) panel.Id = value;
        else if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var declaration in value.Split(';'))
            {
                var p = declaration.Split(':', 2);
                if (p.Length == 2) panel.SetInlineStyle(p[0].Trim(), p[1].Trim());
            }
        }
        else if (name.Equals("disabled", StringComparison.OrdinalIgnoreCase)) panel.IsEnabled = false;
        else if (name.Equals("checked", StringComparison.OrdinalIgnoreCase) && panel is ToggleInput) panel.IsChecked = IsTruthyAttribute(value);
        else if (name.Equals("placeholder", StringComparison.OrdinalIgnoreCase) && panel is TextInput textInput)
        {
            textInput.Placeholder = value;
            panel.Attributes[name] = value;
        }
        else if (name.Equals("tooltip", StringComparison.OrdinalIgnoreCase)) panel.Tooltip = value;
        else if (name.Equals("src", StringComparison.OrdinalIgnoreCase) && panel is Image image)
        {
            image.Source = value;
            panel.Attributes["src"] = value;
        }
        else if (name.Equals("name", StringComparison.OrdinalIgnoreCase) && panel is Icon icon)
            icon.Name = value; // Icon name -> Assets/Icons/<name>.svg
        else panel.Attributes[name] = value;
    }

    /// <summary>Booleans render as "True"/"False" or "true"/"false" from a @bind expression.</summary>
    private static bool IsTruthyAttribute(string value) =>
        !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";

    /// <summary>
    /// Re-activates the component instances of a reused fragment under the
    /// runtime's child-component table so this render pass does not prune them
    /// (EndRenderPass removes children that were not re-created). Without this,
    /// a dirty component nested inside an unchanged fragment — e.g. an enum
    /// editor inside an inspector section whose collapse state did not change —
    /// would be dropped from the component graph and could never re-render.
    /// </summary>
    private static void ReactivateFragmentComponents(RazorPanel runtime, IReadOnlyList<Panel>? panels,
        IReadOnlyDictionary<string, RazorComponentSource>? components)
    {
        if (panels is null)
            return;
        var factory = new RazorComponentFactory(components);
        foreach (var panel in panels)
            ReactivateComponentTree(runtime, panel, factory);
    }

    private static void ReactivateComponentTree(RazorPanel owner, Panel panel, RazorComponentFactory factory)
    {
        if (panel is RazorPanel component)
        {
            owner.MarkChildComponentActive(component);
            if (component.NeedsBuild() || component.NeedsContentRebuild())
                factory.BuildTree(component, force: true);
            foreach (var child in component.ChildrenInternal)
                ReactivateComponentTree(component, child, factory);
            return;
        }

        foreach (var child in panel.ChildrenInternal)
            ReactivateComponentTree(owner, child, factory);
    }

    /// <summary>Normalizes a @ref expression to a plain member name.</summary>
    private static string CleanRefName(string expression)
    {
        var name = RazorComponentFactory.CleanRazorExpression(expression);
        return name.StartsWith("this.", StringComparison.Ordinal) ? name[5..] : name;
    }

    private static void AddSpliceText(Panel parent, string text, RazorPanel runtime, string key, ref int insertIndex)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var textPanel = new Panel { TagName = "text", Text = text };
        textPanel.Key = key;
        if (!string.IsNullOrEmpty(runtime.ScopeId)) textPanel.AddScope(runtime.ScopeId);
        parent.AddChild(textPanel);
        insertIndex++;
    }

    private static List<Panel>? BuildFragmentPanels(List<XNode> nodes, string key, string name, RazorPanel runtime,
        IReadOnlyDictionary<string, RazorComponentSource>? components)
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
        // The input was just built with its current declared value; the same
        // change-detection rule decides whether to keep that or restore the
        // previous in-progress edit (see ApplyTextInputState).
        if (panel is TextInput input && previousTree is not null &&
            previousTree.TryGetValue(key, out var previous) && previous is TextInput previousInput)
            ApplyTextInputState(input, previousInput, input.LastDeclaredValue);

        for (var i = 0; i < panel.Children.Count; i++)
            RestorePreservedInputs(panel.Children[i], $"{key}/{i}", previousTree);
    }

    /// <summary>
    /// Reconciles a rebuilt text input against the input that occupied the same
    /// position before. The in-progress typed value (and caret/focus) is kept
    /// only while the parent still declares the same value; a changed declared
    /// value (a new entity in the inspector, an external update) replaces the
    /// stale text but still inherits focus/caret when the previous input was
    /// focused.
    /// </summary>
    private static void ApplyTextInputState(TextInput input, TextInput? previous, string declared)
    {
        if (previous is null)
        {
            input.SetValueQuiet(declared, declared.Length);
            input.LastDeclaredValue = declared;
            return;
        }

        if (!string.Equals(previous.LastDeclaredValue, declared, StringComparison.Ordinal))
            input.SetValueQuiet(declared, declared.Length);
        else
            input.SetValueQuiet(previous.Value, previous.CaretIndex);

        input.CopyInteractionStateFrom(previous);
        input.LastDeclaredValue = declared;
    }
}
