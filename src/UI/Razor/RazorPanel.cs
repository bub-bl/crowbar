using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace Crowbar.UI;

/// <summary>
/// Base class of every runtime-compiled Razor component. Implements the
/// <see cref="IComponent"/> contract required by the Razor language services,
/// but the native renderer does not use Blazor's render pipeline: the markup
/// produced by <c>ExecuteAsync</c> is parsed into a <see cref="Panel"/> tree.
/// </summary>
public abstract class RazorPanel : PanelComponent, IComponent
{
    void IComponent.Attach(RenderHandle renderHandle)
    {
    }

    Task IComponent.SetParametersAsync(ParameterView parameters) => Task.CompletedTask;

    public string? ScopeId { get; set; }

    /// <summary>
    /// Placeholder emitted by <see cref="Write"/> when a component renders its
    /// default <c>@ChildContent</c> fragment. The native parser replaces it
    /// with the panels captured from the markup between the component's tags.
    /// </summary>
    internal const string ChildContentMarker = "[[__CROWBAR_CHILDCONTENT__]]";

    private const string NamedFragmentMarkerPrefix = "[[__CROWBAR_FRAGMENT__:";

    /// <summary>Returns the marker text emitted for a <see cref="RenderFragment"/> parameter.</summary>
    internal static string FragmentMarker(string name) => name.Equals("ChildContent", StringComparison.OrdinalIgnoreCase)
        ? ChildContentMarker
        : NamedFragmentMarkerPrefix + name + "]]";

    /// <summary>
    /// Panels captured from the markup provided for the component's
    /// <see cref="RenderFragment"/> parameters (named regions like <c>Header</c>
    /// or the default <c>ChildContent</c>), keyed by parameter name.
    /// <see langword="null"/> panels mean the fragment is provided but empty.
    /// </summary>
    private readonly Dictionary<string, (IReadOnlyList<Panel>? Panels, string Signature)> _fragments =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps each live sentinel fragment instance to the parameter it stands for.</summary>
    private readonly Dictionary<RenderFragment, string> _fragmentNames = new();

    private int _fragmentVersion;
    private int _builtFragmentVersion = -1;

    // These two cover every fragment (ChildContent and named regions alike);
    // the names are kept for compatibility with earlier single-fragment builds.
    internal bool NeedsContentRebuild() => _fragmentVersion != _builtFragmentVersion;

    internal void MarkChildContentBuilt() => _builtFragmentVersion = _fragmentVersion;

    internal string? GetFragmentSignature(string name) =>
        _fragments.TryGetValue(name, out var fragment) ? fragment.Signature : null;

    internal IReadOnlyList<Panel>? GetFragmentPanels(string name) =>
        _fragments.TryGetValue(name, out var fragment) ? fragment.Panels : null;

    /// <summary>Names of the fragments currently provided to this component (snapshot).</summary>
    internal string[] ProvidedFragmentNames => [.. _fragments.Keys];

    /// <summary>
    /// True when <paramref name="elementName"/> can be a named child content
    /// region: a writable <c>[Parameter]</c> property of type
    /// <see cref="RenderFragment"/> on this component. Matching follows the
    /// engine's usual case-insensitive parameter resolution, so an element like
    /// <c>&lt;header&gt;</c> is consumed as a <c>Header</c> region when the
    /// component exposes such a fragment parameter.
    /// </summary>
    internal bool HasRenderFragmentParameter(string elementName)
    {
        var property = FindParameter(elementName);
        return property is not null && IsRazorParameter(property) && property.CanWrite &&
               typeof(RenderFragment).IsAssignableFrom(property.PropertyType);
    }

    private readonly StringBuilder _output = new();
    private string _attributeSuffix = string.Empty;
    private readonly Dictionary<string, RazorPanel> _childComponents = new(StringComparer.Ordinal);
    // Component tag (markup element name) each child was created for, so a key
    // whose markup shifted to a different component (e.g. an @if inserts a
    // sibling) is not handed a stale instance of the previous component type.
    private readonly Dictionary<string, string> _childComponentTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeChildren = new(StringComparer.Ordinal);
    private bool _initialized;
    // The rendered panel of a nested component is kept so a child-only render
    // can replace its visual subtree without rebuilding the whole Razor page.
    private Panel? _renderedTree;

    internal void ReplaceRenderedTree(Panel fresh)
    {
        if (ReferenceEquals(_renderedTree, fresh)) return;
        if (_renderedTree?.Parent is { } parent)
            parent.ReplaceChild(_renderedTree, fresh);
        _renderedTree = fresh;
    }

    protected void WriteLiteral(string value) => _output.Append(value);

    protected void Write(object? value)
    {
        // @ChildContent / @Header / ... compile to Write(fragment). The fragment
        // content is captured as panels by the parent's parser; emitting a named
        // placeholder here lets the parser splice them into the child's tree at
        // the right spot. The sentinel instances registered by SetFragment are
        // how Write knows which parameter it is rendering.
        if (value is RenderFragment fragment)
        {
            if (_fragmentNames.TryGetValue(fragment, out var name))
            {
                _output.Append(FragmentMarker(name));
            }
            else if (_fragments.TryGetValue("ChildContent", out var childContent) &&
                     childContent.Panels is not null)
            {
                // Unregistered inline fragment with real ChildContent to splice:
                // keep the legacy fallback so the content still lands somewhere.
                _output.Append(ChildContentMarker);
            }

            return;
        }

        _output.Append(System.Net.WebUtility.HtmlEncode(value?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Captures the panels built from the markup provided for a fragment
    /// parameter (the default <c>ChildContent</c> or a named region like
    /// <c>Header</c>) and binds them to the matching writable <c>[Parameter]</c>
    /// property of type <see cref="RenderFragment"/>. A <see langword="null"/>
    /// (or empty) panel list clears a previously captured fragment so
    /// conditional regions disappear on re-render.
    /// </summary>
    internal void SetFragment(string name, IReadOnlyList<Panel>? panels, string signature)
    {
        _fragmentVersion++;
        _fragments[name] = (panels, signature);
        RemoveFragmentRegistration(name);
        var property = FindParameter(name);
        if (property is not null && !typeof(RenderFragment).IsAssignableFrom(property.PropertyType))
            property = null; // Not a fragment parameter; nothing to bind or reset.
        if (panels is not { Count: > 0 })
        {
            // No (or no longer any) content for this fragment: reset the
            // parameter so @Fragment renders nothing and no stale panels are
            // spliced.
            if (property is not null && IsRazorParameter(property) && property.CanWrite)
                property.SetValue(this, null);
            return;
        }

        if (property is null)
            throw new InvalidOperationException(
                $"{GetType().Name} does not expose a [Parameter] {name} property of type RenderFragment, " +
                "but markup was provided for it.");
        if (!IsRazorParameter(property))
            throw new InvalidOperationException(
                $"Razor property '{name}' on {GetType().Name} is not marked with [Parameter].");
        if (!property.CanWrite)
            throw new InvalidOperationException($"Razor parameter '{name}' on {GetType().Name} is read-only.");
        var sentinel = CreateFragmentSentinel();
        _fragmentNames[sentinel] = name;
        property.SetValue(this, sentinel);
    }

    private void RemoveFragmentRegistration(string name)
    {
        foreach (var key in _fragmentNames.Where(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToArray())
            _fragmentNames.Remove(key);
    }

    private static RenderFragment CreateFragmentSentinel()
    {
        // Each sentinel captures a unique token so delegate equality can never
        // conflate two fragments rendered by the same component.
        var token = Guid.NewGuid();
        return builder => { _ = token; };
    }

    // Helpers emitted by Razor for attributes containing C# expressions,
    // e.g. value="@name". The native renderer still receives plain markup;
    // these methods only reproduce the small writer contract needed by the
    // generated Razor class.
    protected void BeginWriteAttribute(string name, string prefix, int prefixOffset, string suffix, int suffixOffset,
        int attributeValuesCount)
    {
        _attributeSuffix = suffix;
        _output.Append(prefix);
    }

    protected void WriteAttributeValue(string prefix, int prefixOffset, object? value, int valueOffset, int valueLength,
        bool isLiteral)
    {
        _output.Append(prefix);
        var text = value?.ToString() ?? string.Empty;
        _output.Append(isLiteral ? text : System.Net.WebUtility.HtmlEncode(text));
    }

    protected void EndWriteAttribute() => _output.Append(_attributeSuffix);

    protected virtual void OnInitialized()
    {
    }

    protected virtual void OnParametersSet()
    {
    }

    protected virtual void OnAfterRender(bool firstRender)
    {
    }

    internal async Task<string> RenderMarkupAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            OnInitialized();
        }

        OnParametersSet();
        _output.Clear();
        await ExecuteAsync();
        return _output.ToString();
    }

    internal void NotifyRendered(bool firstRender) => OnAfterRender(firstRender);
    internal void BeginRenderPass() => _activeChildren.Clear();

    internal RazorPanel GetOrCreateChild(string key, string tag, Func<RazorPanel> factory)
    {
        // Child keys are positional, so the element occupying a key can change
        // between renders (conditional siblings shift everything below them).
        // Reusing an instance across a type change would apply the old
        // component's parameters to the new component's markup (e.g. a
        // Value attribute hitting a ChildContent-only component), so recreate
        // whenever the tag no longer matches the one the child was built for.
        if (!_childComponents.TryGetValue(key, out var child) ||
            !_childComponentTags.TryGetValue(key, out var existingTag) ||
            !existingTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
        {
            child = factory();
            _childComponents[key] = child;
            _childComponentTags[key] = tag;
        }

        _activeChildren.Add(key);
        return child;
    }

    internal void EndRenderPass()
    {
        foreach (var key in _childComponents.Keys.Where(key => !_activeChildren.Contains(key)).ToArray())
        {
            _childComponents.Remove(key);
            _childComponentTags.Remove(key);
        }
    }

    /// <summary>
    /// Walks the live component graph: this component and every nested child
    /// component, depth-first. The render loop uses it to detect that a
    /// descendant (e.g. the time-bucketed status bar) wants a rebuild even
    /// when the root's own hash is constant, so periodic refreshes are scoped
    /// to the component that needs them.
    /// </summary>
    internal IEnumerable<RazorPanel> EnumerateComponents()
    {
        yield return this;
        foreach (var child in _childComponents.Values)
            foreach (var nested in child.EnumerateComponents())
                yield return nested;
    }

    internal void SetParameter(string name, string value, RazorPanel? expressionOwner = null)
    {
        var property = FindParameter(name);
        if (property is not null)
        {
            if (!IsRazorParameter(property))
                throw new InvalidOperationException(
                    $"Razor property '{name}' on {GetType().Name} is not marked with [Parameter].");
            if (!property.CanWrite)
                throw new InvalidOperationException($"Razor parameter '{name}' on {GetType().Name} is read-only.");
            try
            {
                property.SetValue(this, ConvertParameter(value, property.PropertyType, expressionOwner));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Razor parameter '{name}' on {GetType().Name} could not convert value '{value}'.", ex);
            }

            return;
        }

        throw new InvalidOperationException($"Razor parameter '{name}' was not found on {GetType().Name}.");
    }

    private static object? ConvertParameter(string value, Type type, RazorPanel? expressionOwner)
    {
        if (typeof(RenderFragment).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Razor parameter of type {type.Name} cannot be set from a string attribute; " +
                "pass the content between the component tags instead.");
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            if (expressionOwner is null)
                throw new InvalidOperationException(
                    $"Razor parameter of delegate type {type.Name} requires a component expression owner.");
            return BindDelegate(value, type, expressionOwner);
        }
        return type == typeof(string) ? value : Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type);
    }

    private static Delegate BindDelegate(string expression, Type delegateType, RazorPanel owner)
    {
        var methodName = RazorComponentFactory.CleanRazorExpression(expression);
        if (methodName.StartsWith("this.", StringComparison.Ordinal)) methodName = methodName[5..];
        if (!IsIdentifier(methodName))
            throw new InvalidOperationException(
                $"Razor delegate expression '{expression}' must name a method on {owner.GetType().Name}.");

        var invoke = delegateType.GetMethod("Invoke")
            ?? throw new InvalidOperationException($"Razor parameter type {delegateType.Name} is not a delegate.");
        var parameterTypes = invoke.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        var method = owner.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: parameterTypes, modifiers: null);
        if (method is null || method.ReturnType != invoke.ReturnType)
            throw new InvalidOperationException(
                $"Razor delegate expression '{expression}' does not match {delegateType.Name} on {owner.GetType().Name}.");

        return Delegate.CreateDelegate(delegateType, owner, method, throwOnBindFailure: true);
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsRazorParameter(PropertyInfo property) =>
        property.IsDefined(typeof(Microsoft.AspNetCore.Components.ParameterAttribute), true);

    private PropertyInfo? FindParameter(string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var exact = GetType().GetProperty(name, flags);
        if (exact is not null) return exact;
        // Case-insensitive fallback (route parameters, attribute casing). When
        // several properties collide ignoring case, prefer the one declared on
        // the most derived type (e.g. a generated [Parameter] Id over Panel.Id).
        return GetType().GetProperties(flags | BindingFlags.IgnoreCase)
            .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.DeclaringType == GetType() ? 0 : 1)
            .ThenBy(p => p.Name.Equals(name, StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault();
    }

    // Panels/components captured by @ref during parsing, assigned to the
    // component's fields once the tree is built (see ApplyRefs).
    private readonly List<(string Field, object Target)> _pendingRefs = [];

    internal void AddRef(string field, object target) => _pendingRefs.Add((field, target));

    /// <summary>
    /// Assigns the panels/components captured by <c>@ref</c> to the matching
    /// fields/properties after the tree is built. Element refs receive the
    /// freshly built panel (panels are rebuilt each render); component refs
    /// receive the persistent child component instance.
    /// </summary>
    internal void ApplyRefs()
    {
        foreach (var (field, target) in _pendingRefs)
        {
            var type = GetType();
            var property = type.GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite == true) property.SetValue(this, target);
            else
            {
                var fieldInfo = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                fieldInfo?.SetValue(this, target);
            }
        }
        _pendingRefs.Clear();
    }

    /// <summary>
    /// Resolves the dictionary referenced by <c>@attributes="expr"</c> (a
    /// field or property of the component) into key/value pairs, or null when
    /// the member is missing or not a dictionary.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, object?>>? ResolveAttributes(string expression)
    {
        var name = RazorComponentFactory.CleanRazorExpression(expression);
        if (name.StartsWith("this.", StringComparison.Ordinal)) name = name[5..];
        var value = GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(this)
                    ?? GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(this);
        return value switch
        {
            null => null,
            IReadOnlyDictionary<string, object> readOnlyObject =>
                readOnlyObject.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToArray(),
            IDictionary<string, object> objectDict =>
                objectDict.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToArray(),
            IDictionary<string, string> stringDict =>
                stringDict.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToArray(),
            IEnumerable<KeyValuePair<string, object>> objectPairs =>
                objectPairs.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToArray(),
            IEnumerable<KeyValuePair<string, string>> stringPairs =>
                stringPairs.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToArray(),
            _ => null
        };
    }

    internal Action<string>? NavigationRequested { get; set; }

    protected void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        NavigationRequested?.Invoke(url);
    }

    /// <summary>
    /// Sink for the docked 3D viewport rectangle, wired to the hosting
    /// <see cref="UiSystem"/> (like <see cref="NavigationRequested"/>) and
    /// propagated to child components. A component such as the DockArea calls
    /// <see cref="PublishViewport"/> after laying out so the host can confine
    /// the 3D scene to the viewport instead of the whole window.
    /// </summary>
    internal Action<UiRect?>? ViewportPublishRequested { get; set; }

    /// <summary>Publishes the on-surface rectangle of the docked 3D viewport (null when absent).</summary>
    protected void PublishViewport(UiRect? rect) => ViewportPublishRequested?.Invoke(rect);

    /// <summary>
    /// Sink for the custom title-bar geometry, wired to the hosting
    /// <see cref="UiSystem"/> (like <see cref="NavigationRequested"/> and
    /// <see cref="ViewportPublishRequested"/>) and propagated to child
    /// components. The TopBar publishes its caption layout so the platform can
    /// answer WM_NCHITTEST for the native caption behavior.
    /// </summary>
    internal Action<WindowChromeLayout?>? WindowChromePublishRequested { get; set; }

    /// <summary>Publishes the custom title-bar geometry (null when there is no title bar).</summary>
    protected void PublishWindowChrome(WindowChromeLayout? layout) => WindowChromePublishRequested?.Invoke(layout);

    /// <summary>
    /// Host → UI channel for the live window-chrome state (hovered caption
    /// button, maximized, active, fullscreen, custom-chrome support), mirrored
    /// by the host each frame. Wired like <see cref="WindowChromePublishRequested"/>
    /// and propagated to child components; a component such as the TopBar reads
    /// <see cref="ChromeState"/> to render the caption feedback.
    /// </summary>
    internal Func<WindowChromeState>? WindowChromeStateProvider { get; set; }

    /// <summary>
    /// The current window-chrome state, or <see cref="WindowChromeState.Default"/>
    /// before the host has wired a provider. Components hash the fields they
    /// render so the caption feedback updates when the platform state changes.
    /// </summary>
    protected WindowChromeState ChromeState =>
        WindowChromeStateProvider?.Invoke() ?? WindowChromeState.Default;

    // The Razor SDK generates a design-time declaration for .razor files.
    // That declaration contains the component shape but not the generated
    // ExecuteAsync body, so the base must remain instantiable from the IDE's
    // point of view. Runtime-compiled components override this method with
    // the real Razor output.
    public virtual Task ExecuteAsync() => Task.CompletedTask;
}

/// <summary>Compatibility name for components compiled by earlier versions.</summary>
public abstract class RazorTemplateBase : RazorPanel
{
}

public interface IRazorComponentCompiler
{
    PanelComponent Compile(string razorSource, string className, Type baseType, params Assembly[] references);
}
