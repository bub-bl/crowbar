using System.IO;

namespace Crowbar.FileSystems;

/// <summary>
/// Convenience read/write helpers shared by every <see cref="IFileSystem"/>
/// backend. Backends only implement the primitives on <see cref="IFileSystem"/>;
/// these composition helpers (read-all, write-all, shared-read open) are
/// available to all of them for free.
/// </summary>
internal static class FileSystemExtensions
{
    /// <summary>Reads a whole text file (UTF-8 with BOM detection).</summary>
    public static string ReadAllText(this IFileSystem fs, FilePath path)
    {
        using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads every line of a text file (UTF-8 with BOM detection).</summary>
    public static string[] ReadAllLines(this IFileSystem fs, FilePath path)
    {
        using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
    }

    /// <summary>Reads a whole file into a byte array.</summary>
    public static byte[] ReadAllBytes(this IFileSystem fs, FilePath path)
    {
        using var stream = fs.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes a byte array to a file, creating the parent directory when missing.</summary>
    public static void WriteAllBytes(this IFileSystem fs, FilePath path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var directory = path.GetDirectory();
        if (!directory.IsEmpty && !fs.DirectoryExists(directory))
            fs.CreateDirectory(directory);
        using var stream = fs.OpenFile(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
    }

    /// <summary>Opens a file for reading with shared write access (external tools may overwrite it while it is open).</summary>
    public static Stream OpenRead(this IFileSystem fs, FilePath path)
        => fs.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
