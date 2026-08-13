namespace Crowbar.UI;

/// <summary>A routable Razor page declared with the <c>@page</c> directive.</summary>
public sealed record PageRoute(string Template, string TagName, string RazorPath, string ClassName);

public sealed partial class UiSystem : IDisposable
{
    public ScreenPanel Screen { get; } = new();
    public SkiaUiRenderer Renderer { get; } = new();
    public Panel? Content { get; private set; }
    private readonly Dictionary<string, StyleSheet> _scopedStyleSheets = new(StringComparer.OrdinalIgnoreCase);
    public StyleSheet? GlobalStyleSheet { get; private set; }
    public StyleSheet StyleSheet { get; private set; } = new();
    public bool IsDirty => Renderer.IsDirty || Screen.LayoutDirty || Screen.AnyPaintDirty || Screen.AnyStyleDirty || Screen.AnyInheritedDirty || Screen.Layout is { Width: 0 };
    private RazorPanel? _razorRoot;
    private RazorComponentFactory? _razorFactory;
    private bool _razorRenderPending;
    private readonly Dictionary<string, RazorComponentSource> _razorComponents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PageRoute> _pages = [];
    private readonly List<PageRoute> _manualPages = [];
    private readonly Dictionary<string, string> _directoryTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime WriteTime, string Text)> _textCache = new(StringComparer.Ordinal);
    private PageRoute? _currentRoute;

    /// <summary>URL of the currently displayed page, or <c>/</c> before any navigation.</summary>
    public string CurrentUrl { get; private set; } = "/";

    /// <summary>
    /// The cursor the platform should show while the pointer is over the UI:
    /// the deepest hovered panel with an explicit <c>cursor</c> style wins
    /// (resolved in <c>ProcessPointerMove</c>). The host reads this each frame
    /// and applies it to the OS cursor.
    /// </summary>
    public string HoveredCursor { get; private set; } = "auto";
    /// <summary>Raised after a navigation, with the new URL.</summary>
    public event Action<string>? NavigationChanged;
    /// <summary>All routes discovered from <c>@page</c> directives.</summary>
    public IReadOnlyList<PageRoute> Pages => _pages;

    public void RegisterRazorComponent(string tagName, string source, string className, string? cssSource = null)
    {
        var scopeId = $"b-{className.ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(cssSource))
        {
            LoadScopedStyles(tagName, cssSource, scopeId);
        }
        var factory = new RazorComponentFactory();
        var typeParameters = RazorComponentFactory.TypeParamNamesFromSource(source);
        _razorComponents[tagName] = new RazorComponentSource(typeParameters, typeArguments =>
        {
            var template = typeArguments is null
                ? factory.CompileTemplate(source, className, typeof(PanelComponent), typeof(UiSystem).Assembly)
                : factory.CompileTemplate(source, className, typeof(PanelComponent), typeArguments, typeof(UiSystem).Assembly);
            template.ScopeId = scopeId;
            return template;
        });
    }

    public void RegisterRazorComponentFromFile(string tagName, string razorPath, string className)
    {
        razorPath = Path.GetFullPath(razorPath);
        RegisterRazorComponentFromFileCore(tagName, razorPath, className);
        foreach (var route in RazorComponentFactory.ExtractPages(ReadStableTextCached(razorPath)))
        {
            var page = new PageRoute(route, tagName, razorPath, className);
            if (_manualPages.All(existing => existing != page)) _manualPages.Add(page);
        }
        RebuildPages();
    }

    private void RegisterRazorComponentFromFileCore(string tagName, string razorPath, string className)
    {
        var scopeId = $"b-{className.ToLowerInvariant()}";
        var cssPath = GetAssociatedCssPath(razorPath);
        if (File.Exists(cssPath)) LoadScopedStyles(tagName, ReadStableTextCached(cssPath), scopeId);
        var fileFactory = new RazorComponentFactory();
        var typeParameters = RazorComponentFactory.TypeParamNamesFromSource(File.Exists(razorPath) ? ReadStableTextCached(razorPath) : string.Empty);
        _razorComponents[tagName] = new RazorComponentSource(typeParameters, typeArguments =>
        {
            var template = fileFactory.CompileTemplateFromFile(razorPath, className, typeof(PanelComponent),
                typeArguments, typeof(UiSystem).Assembly);
            template.ScopeId = scopeId;
            return template;
        });
    }

    /// <summary>
    /// Discovers every .razor file under the given directory and registers it as
    /// a reusable component keyed by file name (e.g. <c>MyButton.razor</c>
    /// becomes the <c>&lt;MyButton&gt;</c> tag). Files whose name starts with an
    /// underscore (such as <c>_Imports.razor</c>) are skipped. Files that declare
    /// an <c>@page</c> directive are additionally registered as routable pages.
    /// The scan is idempotent: calling it again refreshes the registrations, so it
    /// can be re-run on every file change for hot reload.
    /// </summary>
    public int RegisterRazorComponentsFromDirectory(string directory, bool recursive = true)
    {
        directory = Path.GetFullPath(directory);
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.razor", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToArray()
            : [];
        var seen = new List<(string Tag, string Path)>();
        foreach (var razorPath in files)
        {
            var fileName = Path.GetFileName(razorPath);
            if (fileName.StartsWith("_", StringComparison.Ordinal)) continue;
            seen.Add((Path.GetFileNameWithoutExtension(fileName), Path.GetFullPath(razorPath)));
        }
        foreach (var collision in seen.GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new InvalidOperationException($"Duplicate Razor component tag '{collision.Key}' (files differ only by case): {string.Join(", ", collision.Select(item => item.Path))}.");
        foreach (var tag in _directoryTags.Keys.Where(tag => seen.All(item => !string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            _razorComponents.Remove(tag);
            _directoryTags.Remove(tag);
        }
        foreach (var (tag, path) in seen)
        {
            if (!_directoryTags.ContainsKey(tag) && _razorComponents.ContainsKey(tag))
                throw new InvalidOperationException($"Duplicate Razor component tag '{tag}' found at '{path}' (already registered).");
            RegisterRazorComponentFromFileCore(tag, path, tag);
            _directoryTags[tag] = path;
        }
        RebuildPages();
        return seen.Count;
    }

    /// <summary>
    /// Compiles every registered component ahead of the first render, in
    /// parallel. The Roslyn compilations are independent (each references only
    /// the fixed platform assemblies, never another generated component), so a
    /// multi-core machine compiles the whole UI in a fraction of the serial
    /// time — and the emitted assemblies land in the shared in-memory and disk
    /// caches, so the first <see cref="Navigate"/> is only cache hits. Generic
    /// (<c>@typeparam</c>) components are skipped here and compiled lazily when
    /// used with their type arguments.
    /// </summary>
    public void PrecompileAll()
    {
        var sources = _razorComponents.Values.ToArray();
        Parallel.ForEach(sources, source =>
        {
            try
            {
                source.Create(null);
            }
            catch
            {
                // A component that cannot compile standalone (missing type
                // arguments, a bad import) must not abort startup: it will
                // surface its real error when it is actually used.
            }
        });
    }

    private void RebuildPages()
    {
        _pages.Clear();
        _pages.AddRange(_manualPages);
        foreach (var (tag, path) in _directoryTags)
        {
            foreach (var route in RazorComponentFactory.ExtractPages(ReadStableTextCached(path)))
                _pages.Add(new PageRoute(route, tag, path, tag));
        }
    }

    public void SetViewport(int width, int height)
    {
        Renderer.Resize(width, height);
        // Media queries are evaluated against the viewport: record it on every
        // sheet so the next cascade applies the right rules, then force a full
        // re-cascade (resizes are rare).
        GlobalStyleSheet?.SetViewport(width, height);
        foreach (var sheet in _scopedStyleSheets.Values) sheet.SetViewport(width, height);
        StyleSheet.SetViewport(width, height);
        Screen.Invalidate();
        Renderer.MarkDirty();
    }

    public void LoadRazorFromFile(string razorPath, string className = "Root")
    {
        razorPath = Path.GetFullPath(razorPath);
        var source = File.ReadAllText(razorPath);
        var scopeId = $"b-{className.ToLowerInvariant()}";
        var scopedCssPath = GetAssociatedCssPath(razorPath);
        if (File.Exists(scopedCssPath))
        {
            var css = File.ReadAllText(scopedCssPath);
            LoadScopedStyles(scopedCssPath, css, scopeId);
        }
        LoadRazor(source, className);
    }

    public void LoadRazor(string source, string className = "Root")
    {
        _razorFactory = new RazorComponentFactory(_razorComponents);
        _razorRoot = _razorFactory.CompileTemplate(source, className, typeof(PanelComponent), typeof(UiSystem).Assembly);
        _razorRoot.StateChanged = () => _razorRenderPending = true;
        _razorRoot.NavigationRequested = Navigate;
        _currentRoute = null;
        SetContent(_razorFactory.BuildTree(_razorRoot));
    }

    public void LoadRazor(string source, string className, string cssSource)
    {
        var scopeId = $"b-{className.ToLowerInvariant()}";
        LoadScopedStyles(className, cssSource, scopeId);
        LoadRazor(source, className);
    }

    public void LoadStyles(string css)
    {
        GlobalStyleSheet = StyleSheet.Parse(css);
        RebuildCombinedStyleSheet();
    }

    public void LoadScopedStyles(string key, string css, string scopeId)
    {
        _scopedStyleSheets[key] = StyleSheet.Parse(css, scopeId);
        RebuildCombinedStyleSheet();
    }

    private void RebuildCombinedStyleSheet()
    {
        var combined = new StyleSheet();
        if (GlobalStyleSheet is not null)
        {
            combined.AddRules(GlobalStyleSheet.Rules);
        }
        foreach (var sheet in _scopedStyleSheets.Values)
        {
            combined.AddRules(sheet.Rules);
        }
        StyleSheet = combined;
        Renderer.StyleSheet = StyleSheet;
        // A stylesheet swap can change any computed property, so force a full
        // cascade + layout pass (style-sheet loads are rare).
        Screen.Invalidate();
        Renderer.MarkDirty();
    }

    public static string GetAssociatedCssPath(string razorPath)
    {
        if (razorPath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return razorPath + ".css";
        return Path.ChangeExtension(razorPath, ".razor.css");
    }

    /// <summary>
    /// Runs only the style/layout passes (no raster) so the GPU tree-walk
    /// painter can record the laid-out tree, and handles the deferred Razor
    /// rebuild exactly like <see cref="Render"/>. Returns false when nothing
    /// changed and the frame can be skipped.
    /// </summary>
    public bool Prepare()
    {
        var changed = Renderer.PrepareForGpu(Screen);
        if (_razorRenderPending && Screen.Layout.Width > 0 && Screen.Layout.Height > 0)
        {
            RenderRazorIfNeeded();
            changed |= Renderer.PrepareForGpu(Screen);
        }
        return changed;
    }

    public ReadOnlyMemory<byte> Render()
    {
        var pixels = Renderer.Render(Screen);

        // A component can request its first geometry-dependent render while the
        // tree is being built, before the renderer has assigned layout rects.
        // Process that deferred request after the first layout pass, then paint
        // the resulting tree. This keeps the normal Update -> Render frame order
        // from displaying an empty DockArea on startup.
        if (_razorRenderPending && Screen.Layout.Width > 0 && Screen.Layout.Height > 0)
        {
            RenderRazorIfNeeded();
            pixels = Renderer.Render(Screen);
        }

        return pixels;
    }

    /// <summary>
    /// Rebuilds the Razor tree when the root, or any component nested under it,
    /// wants one. Checking the descendants (not just the root) lets a
    /// component with a periodic hash — the status bar's time-bucketed
    /// BuildHash — drive its own refresh: the page itself has a constant hash,
    /// so the whole tree is not rebuilt on a global timer while nothing
    /// changed.
    /// </summary>
    internal void RenderRazorIfNeeded()
    {
        if (_razorRoot is null) return;

        // A rebuild is due when the root was explicitly asked to re-render, or
        // when any component in the tree wants one (the status bar's
        // time-bucketed BuildHash is the usual requestor). A hash-only update on
        // a descendant can be rendered in place: rebuilding the page root would
        // tear down and lay out the entire editor several times per second.
        var dirtyComponents = _razorRoot.EnumerateComponents()
            .Where(component => component.NeedsBuild() || component.NeedsContentRebuild())
            .ToArray();
        var rootNeedsBuild = dirtyComponents.Contains(_razorRoot);
        if (!_razorRenderPending && dirtyComponents.Length == 0) return;
        if (!rootNeedsBuild && dirtyComponents.Length > 0)
        {
            // On startup the engine updates before its first render. A nested
            // component can still have a zero layout at that point; rebuilding
            // it now would replace the initial DockArea output with a tree whose
            // geometry-dependent groups have not been emitted yet. Let the
            // renderer perform the first layout, then retry on the next update.
            if (Screen.LayoutDirty ||
                Screen.Layout.Width > 0 && Screen.Layout.Height > 0 &&
                dirtyComponents.Any(component => component.Layout.Width <= 0 || component.Layout.Height <= 0))
                return;

            // A descendant can request a render without invalidating the page
            // root. Rebuild only the dirty component; rebuilding the editor page
            // here would recreate every dock wrapper and force a full layout.
            var descendantFactory = _razorFactory ?? new RazorComponentFactory(_razorComponents);
            foreach (var component in dirtyComponents)
            {
                if (component.NeedsBuild() || component.NeedsContentRebuild())
                    descendantFactory.BuildTree(component, force: true);
            }

            _razorRenderPending = false;
            return;
        }
        var factory = _razorFactory ?? new RazorComponentFactory(_razorComponents);
        if (!_razorRoot.CanRender())
        {
            _razorRoot.MarkRenderSkipped();
            _razorRenderPending = false;
            return;
        }

        _razorRenderPending = false;
        SetContent(factory.BuildTree(_razorRoot, force: true));
    }

    /// <summary>Navigates to the page whose <c>@page</c> route matches <paramref name="url"/>.</summary>
    public void Navigate(string url)
    {
        url = NormalizeUrl(url);
        if (TryResolveRoute(url, out var route, out var routeParams))
        {
            var factory = new RazorComponentFactory(_razorComponents);
            var template = factory.CompileTemplateFromFile(route.RazorPath, route.ClassName, typeof(PanelComponent), typeof(UiSystem).Assembly);
            foreach (var (name, value) in routeParams) template.SetParameter(name, value);
            template.StateChanged = () => _razorRenderPending = true;
            template.NavigationRequested = Navigate;
            _razorFactory = factory;
            _razorRoot = template;
            _currentRoute = route;
            CurrentUrl = url;
            SetContent(factory.BuildTree(template));
        }
        else
        {
            CurrentUrl = url;
            ShowNotFound(url);
        }
        NavigationChanged?.Invoke(CurrentUrl);
    }

    private void ShowNotFound(string url)
    {
        _razorRoot = null;
        _currentRoute = null;
        var page = new Panel { TagName = "div" };
        page.AddClass("not-found");
        page.AddChild(new Label("404"));
        page.AddChild(new Label("Nothing at " + url));
        SetContent(page);
    }

    private void SetContent(Panel newContent)
    {
        var old = Content;
        var oldFocused = FocusedPanel;
        Content = newContent;
        if (old is not null) Screen.RemoveChild(old);
        Screen.AddChild(newContent);
        if (oldFocused is TextInput && FindPanel<TextInput>(newContent) is { } replacement)
        {
            replacement.SetFocused(true);
            FocusedPanel = replacement;
        }
        Renderer.MarkDirty();
    }

    private bool TryResolveRoute(string url, out PageRoute route, out Dictionary<string, string> routeParams)
    {
        route = null!;
        routeParams = [];
        var bestScore = -1;
        foreach (var page in _pages)
        {
            if (TryMatchRoute(page.Template, url, out var parameters, out var score) && score > bestScore)
            {
                bestScore = score;
                route = page;
                routeParams = parameters;
            }
        }
        return bestScore >= 0;
    }

    private static bool TryMatchRoute(string template, string url, out Dictionary<string, string> routeParams, out int score)
    {
        routeParams = [];
        score = 0;
        var templateSegments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var urlSegments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (templateSegments.Length == 0) return urlSegments.Length == 0;
        for (var i = 0; i < templateSegments.Length; i++)
        {
            var segment = templateSegments[i];
            if (segment.StartsWith("{**", StringComparison.Ordinal) && segment.EndsWith('}') && i == templateSegments.Length - 1)
            {
                routeParams[segment[3..^1]] = Uri.UnescapeDataString(string.Join("/", urlSegments.Skip(i)));
                score += 1;
                return true;
            }
            if (i >= urlSegments.Length) { routeParams = []; return false; }
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var name = segment[1..^1];
                var constraintStart = name.IndexOf(':');
                var constraint = constraintStart >= 0 ? name[(constraintStart + 1)..] : null;
                if (constraintStart >= 0) name = name[..constraintStart];
                if (constraint is not null && !SatisfiesConstraint(constraint, urlSegments[i])) { routeParams = []; return false; }
                routeParams[name] = Uri.UnescapeDataString(urlSegments[i]);
                score += 1;
            }
            else if (string.Equals(segment, urlSegments[i], StringComparison.OrdinalIgnoreCase)) score += 2;
            else { routeParams = []; return false; }
        }
        return templateSegments.Length == urlSegments.Length;
    }

    private static bool SatisfiesConstraint(string constraint, string value) => constraint.ToLowerInvariant() switch
    {
        "string" => true,
        "int" => int.TryParse(value, out _),
        "long" => long.TryParse(value, out _),
        "double" => double.TryParse(value, out _),
        "bool" => bool.TryParse(value, out _),
        "guid" => Guid.TryParse(value, out _),
        "datetime" => DateTime.TryParse(value, out _),
        _ => true // Unknown constraints are ignored, mirroring route matching leniency.
    };

    private static string NormalizeUrl(string url)
    {
        url = (url ?? "/").Trim();
        var queryStart = url.IndexOfAny(['?', '#']);
        if (queryStart >= 0) url = url[..queryStart];
        if (!url.StartsWith('/')) url = "/" + url;
        url = url.TrimEnd('/');
        return url.Length == 0 ? "/" : url;
    }

    private string ReadStableTextCached(string path)
    {
        path = Path.GetFullPath(path);
        var writeTime = GetWriteTime(path);
        if (_textCache.TryGetValue(path, out var entry) && entry.WriteTime == writeTime) return entry.Text;
        var text = ReadStableText(path);
        _textCache[path] = (writeTime, text);
        return text;
    }

    private static T? FindPanel<T>(Panel panel) where T : Panel
    {
        if (panel is T match) return match;
        foreach (var child in panel.Children)
            if (FindPanel<T>(child) is { } nested) return nested;
        return null;
    }
    public void Dispose() { StopWatching(); Renderer.Dispose(); }
}
