using Crowbar.FileSystems;


namespace Crowbar.UI.Tests.Files;

/// <summary>
/// Locks in the read-only view used to expose <see cref="FileSystem.Content"/>:
/// reads pass through while every mutation throws.
/// </summary>
public class ReadOnlyFileSystemTests
{
    [Fact]
    public void ReadOnlyFileSystem_AllowsReads()
    {
        using var writable = ZioFileSystem.Memory();
        var fs = new ReadOnlyFileSystem(writable);
        var file = new FilePath("/data.bin");

        writable.WriteAllBytes(file, [1, 2, 3]);

        Assert.True(fs.FileExists(file));
        Assert.Equal(new byte[] { 1, 2, 3 }, fs.ReadAllBytes(file));
        Assert.Equal("abc", WriteAndReadText(writable, fs));
        Assert.True(fs.CanWatch(new FilePath("/")));
    }

    [Fact]
    public void ReadOnlyFileSystem_BlocksMutations()
    {
        using var writable = ZioFileSystem.Memory();
        var fs = new ReadOnlyFileSystem(writable);
        var file = new FilePath("/data.bin");
        writable.WriteAllBytes(file, [1, 2, 3]);

        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateDirectory("/new-dir"));
        Assert.Throws<UnauthorizedAccessException>(() => fs.DeleteFile(file));
        Assert.Throws<UnauthorizedAccessException>(() => fs.WriteAllBytes(file, [9]));
        Assert.Throws<UnauthorizedAccessException>(
            () => fs.OpenFile(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    private static string WriteAndReadText(IFileSystem writable, ReadOnlyFileSystem readOnly)
    {
        var file = new FilePath("/text.txt");
        writable.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes("abc"));
        return readOnly.ReadAllText(file);
    }
}
