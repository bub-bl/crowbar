using System.IO;
using Crowbar.FileSystems;
using Xunit;

namespace Crowbar.UI.Tests.Files;

/// <summary>
/// Locks in the path-bridge semantics of <see cref="FileSystemService"/>: a
/// leading <c>/</c> is a logical absolute path (a mount point), never an OS
/// path — only fully-qualified OS paths (drive letter / UNC) bypass the mount
/// table and content root.
/// </summary>
public class FileSystemServiceTests
{
    [Theory]
    [InlineData("/Ui")]
    [InlineData("/Game/DemoComponent.cs")]
    [InlineData("Shaders/Surface/StandardPbr.wgsl")]
    public void IsRooted_LogicalPathsAreNotOsRooted(string path)
        => Assert.False(FileSystemService.IsRooted(path));

    [Fact]
    public void IsRooted_FullyQualifiedOsPathIsRooted()
        => Assert.True(FileSystemService.IsRooted(Path.GetFullPath(".")));

    [Fact]
    public void ToFilePath_LeadingSlashResolvesThroughMount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crowbar-fs-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new FileSystemService(ZioFileSystem.Physical(), AppContext.BaseDirectory,
                new Dictionary<FilePath, string> { ["/Ui"] = tempDir });

            // The mount must win over treating "/Ui" as an OS root (C:\Ui).
            var resolved = service.ToSystemPath("/Ui/Editor.razor");
            Assert.Equal(Path.Combine(tempDir, "Editor.razor"), resolved);

            // And the mount actually reads/writes through the target directory.
            var file = Path.Combine(tempDir, "probe.txt");
            File.WriteAllText(file, "hello");
            Assert.Equal("hello", service.ReadAllText("/Ui/probe.txt"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
