namespace Crowbar.Engine.Tests;

public class ShaderPreprocessorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "crowbar-shader-tests-" + Guid.NewGuid().ToString("N"));

    public ShaderPreprocessorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Includes_AreFlattenedIntoTheSource()
    {
        Write("Common/Util.wgsl", "fn utilValue() -> f32 { return 42.0; }\n");
        var shader = Shader.Load(Write("Main.wgsl", """
            #include "Common/Util.wgsl"

            @vertex
            fn vs_main() {}
            """));

        Assert.Contains("fn utilValue()", shader.Source);
        Assert.Contains("@vertex", shader.Source);
        Assert.DoesNotContain("#include", shader.Source);
    }

    [Fact]
    public void NestedIncludes_ResolveRelativeToTheIncludingFile()
    {
        Write("Common/A.wgsl", "#include \"B.wgsl\"\nfn a() {}\n");
        Write("Common/B.wgsl", "fn b() {}\n");
        var shader = Shader.Load(Write("Main.wgsl", "#include \"Common/A.wgsl\"\n"));

        Assert.Contains("fn a()", shader.Source);
        Assert.Contains("fn b()", shader.Source);
    }

    [Fact]
    public void CircularIncludes_Throw()
    {
        var a = Write("A.wgsl", "#include \"B.wgsl\"\nfn a() {}\n");
        Write("B.wgsl", "#include \"A.wgsl\"\nfn b() {}\n");

        var exception = Assert.Throws<InvalidOperationException>(() => Shader.Load(a));
        Assert.Contains("Circular", exception.Message);
    }

    [Fact]
    public void MissingInclude_Throws()
    {
        var shader = Write("A.wgsl", "#include \"DoesNotExist.wgsl\"\n");

        Assert.Throws<FileNotFoundException>(() => Shader.Load(shader));
    }
}
