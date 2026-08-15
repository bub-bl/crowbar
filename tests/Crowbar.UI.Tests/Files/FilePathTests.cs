using Crowbar.Files;

namespace Crowbar.UI.Tests.Files;

/// <summary>
/// Locks in the normalization and path-operation semantics of the engine's own
/// <see cref="FilePath"/> type (uniform <c>/</c> separator, <c>.</c>/<c>..</c>
/// collapsing, extension handling), which no longer delegates to a third-party
/// path type.
/// </summary>
public class FilePathTests
{
    [Theory]
    [InlineData(@"C:\foo\bar\", "C:/foo/bar")]   // backslashes become /, trailing separator dropped
    [InlineData("a//b///c", "a/b/c")]            // repeated separators collapse
    [InlineData("/a/../b", "/b")]                // .. pops a parent
    [InlineData("a/b/../../c", "c")]             // .. across the whole path
    [InlineData("..", "..")]                     // leading .. on a relative path is kept
    [InlineData("../a", "../a")]
    [InlineData(".", "")]                        // a lone . is empty
    [InlineData("/../a", "/a")]                  // .. at the absolute root is dropped
    public void Normalizes(string input, string expected)
        => Assert.Equal(expected, new FilePath(input).FullName);

    [Fact]
    public void EmptyAndRoot()
    {
        Assert.Equal("", FilePath.Empty.FullName);
        Assert.Equal("/", FilePath.Root.FullName);
        Assert.True(FilePath.Empty.IsEmpty);
        Assert.True(FilePath.Root.IsAbsolute);
        Assert.True(new FilePath("a").IsRelative);
    }

    [Fact]
    public void Combine_AbsoluteRightWins()
    {
        Assert.Equal("/b", (new FilePath("a") / new FilePath("/b")).FullName);
        Assert.Equal("a/b", (new FilePath("a") / new FilePath("b")).FullName);
        Assert.Equal("a", (new FilePath("a") / FilePath.Empty).FullName);
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("a/b", "a")]
    [InlineData("/a", "/")]
    [InlineData("a", "")]
    public void GetDirectory(string input, string expected)
        => Assert.Equal(expected, new FilePath(input).GetDirectory().FullName);

    [Fact]
    public void NameParts()
    {
        var path = new FilePath("/a/b.txt");
        Assert.Equal("b.txt", path.GetName());
        Assert.Equal("b", path.GetNameWithoutExtension());
        Assert.Equal(".txt", path.GetExtensionWithDot());
        Assert.Equal("/a/b.css", path.ChangeExtension(".css").FullName);
        Assert.Equal("/a/b", path.ChangeExtension(null).FullName);
    }

    [Fact]
    public void HiddenNameHasNoExtension()
    {
        var path = new FilePath("/a/.gitignore");
        Assert.Equal(".gitignore", path.GetNameWithoutExtension());
        Assert.Equal("", path.GetExtensionWithDot());
    }

    [Theory]
    [InlineData("a/b/c", "a/b", true, true)]
    [InlineData("a/b/c/d", "a/b", false, false)]
    [InlineData("a/bc", "a/b", true, false)]   // segment boundary, not a string prefix
    [InlineData("a/b", "a/b", true, true)]     // exact match
    public void IsInDirectory(string path, string directory, bool recursive, bool expected)
        => Assert.Equal(expected, new FilePath(path).IsInDirectory(directory, recursive));

    [Fact]
    public void AbsoluteRelativeRoundTrip()
    {
        Assert.Equal("/a", new FilePath("a").ToAbsolute().FullName);
        Assert.Equal("a", new FilePath("/a").ToRelative().FullName);
        Assert.Equal("", new FilePath("/").ToRelative().FullName);
    }
}
