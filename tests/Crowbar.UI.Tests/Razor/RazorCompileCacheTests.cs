using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

/// <summary>
/// Compiled Razor components are persisted to a content-hashed disk cache so
/// launches (and duplicate content) skip the Roslyn emit.
///
/// The cache directory is a process-wide static (the point of a cross-launch
/// cache), so parallel tests in other collections write their entries into
/// whatever directory is current. These tests therefore assert only on the
/// exact files their own compiles produce (the content hash is computed the
/// same way the compiler does), never on the directory as a whole.
/// </summary>
public class RazorCompileCacheTests : IDisposable
{
    private readonly TempDirectory _tmp = TestUi.TempDir("razor-cache");
    private readonly string _originalCacheDir = RazorComponentFactory.RazorCacheDirectory;

    public RazorCompileCacheTests()
    {
        RazorComponentFactory.RazorCacheDirectory = _tmp.Path;
    }

    public void Dispose()
    {
        RazorComponentFactory.RazorCacheDirectory = _originalCacheDir;
        _tmp.Dispose();
    }

    private const string Source = """
        @inherits Crowbar.UI.RazorPanel
        <div class="card">cache test</div>
        """;

    private static string CacheFile(string source, string className) =>
        Path.Combine(
            RazorComponentFactory.RazorCacheDirectory,
            RazorComponentFactory.ComputeCacheHash(source, className, typeof(RazorPanel), []) + ".dll");

    private static RazorPanel CompileFrom(string path, string className)
    {
        var factory = new RazorComponentFactory();
        return factory.CompileTemplateFromFile(path, className, typeof(RazorPanel));
    }

    [Fact]
    public void IdenticalContentIsServedFromDiskWithoutRecompiling()
    {
        var first = Path.Combine(_tmp.Path, "First.razor");
        var second = Path.Combine(_tmp.Path, "Second.razor");
        File.WriteAllText(first, Source);
        File.WriteAllText(second, Source);

        var entry = CacheFile(Source, "SameClass");
        var firstTemplate = CompileFrom(first, "SameClass");
        Assert.NotNull(firstTemplate);
        Assert.True(File.Exists(entry), $"expected cache entry {entry}");
        var writeTime = File.GetLastWriteTimeUtc(entry);

        // Give a hypothetical recompile time to leave a trace: a fresh emit
        // would overwrite the entry and bump its write time. A disk hit leaves
        // the file untouched.
        Thread.Sleep(20);
        var secondTemplate = CompileFrom(second, "SameClass");

        Assert.NotNull(secondTemplate);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(entry));
    }

    [Fact]
    public void ChangedContentGetsItsOwnCacheEntry()
    {
        var first = Path.Combine(_tmp.Path, "First.razor");
        var second = Path.Combine(_tmp.Path, "Second.razor");
        File.WriteAllText(first, Source);
        File.WriteAllText(second, Source + "\n<div>extra</div>\n");

        CompileFrom(first, "SameClass");
        CompileFrom(second, "SameClass");

        var firstEntry = CacheFile(Source, "SameClass");
        var secondEntry = CacheFile(Source + "\n<div>extra</div>\n", "SameClass");
        Assert.True(File.Exists(firstEntry), $"expected cache entry {firstEntry}");
        Assert.True(File.Exists(secondEntry), $"expected cache entry {secondEntry}");
        Assert.NotEqual(firstEntry, secondEntry);
    }

    [Fact]
    public void DifferentClassNameGetsItsOwnCacheEntry()
    {
        var path = Path.Combine(_tmp.Path, "Card.razor");
        File.WriteAllText(path, Source);

        CompileFrom(path, "Card");
        CompileFrom(path, "OtherCard");

        Assert.True(File.Exists(CacheFile(Source, "Card")), "expected entry for class Card");
        Assert.True(File.Exists(CacheFile(Source, "OtherCard")), "expected entry for class OtherCard");
    }

    [Fact]
    public void PrecompileAllWarmsTheEditorComponents()
    {
        var uiDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "src", "Editor", "Ui"));
        Assert.True(Directory.Exists(uiDir), $"Ui directory not found: {uiDir}");
        using var ui = new UiSystem();
        ui.RegisterRazorComponentsFromDirectory(uiDir);
        ui.PrecompileAll(); // must not throw on the real component set
        ui.Navigate("/editor");
        ui.Prepare();
        Assert.NotNull(ui.Content);
    }
}
