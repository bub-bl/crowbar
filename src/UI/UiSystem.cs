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

    public ReadOnlyMemory<byte> Render() => Renderer.Render(Screen);
    internal void RenderRazorIfNeeded()
    {
        if (_razorRoot is null || (!_razorRenderPending && !_razorRoot.NeedsBuild())) return;
        if (!_razorRoot.CanRender())
        {
            _razorRoot.MarkRenderSkipped();
            _razorRenderPending = false;
            return;
        }
        _razorRenderPending = false;
        SetContent((_razorFactory ?? new RazorComponentFactory(_razorComponents)).BuildTree(_razorRoot));
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
