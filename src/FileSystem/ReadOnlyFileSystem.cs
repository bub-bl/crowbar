using System.IO;

namespace Crowbar.FileSystems;

/// <summary>
/// A read-only view over another <see cref="IFileSystem"/>: reads, enumeration,
/// path conversion and watching are delegated, while mutations
/// (<see cref="OpenFile"/> for writing, <see cref="CreateDirectory"/>,
/// <see cref="DeleteFile"/>) throw. The base game content
/// (<see cref="FileSystem.Content"/>) is exposed through this so a game project can
/// read it but never overwrite it.
/// </summary>
internal sealed class ReadOnlyFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    public ReadOnlyFileSystem(IFileSystem inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public FilePath ConvertPathFromInternal(string systemPath) => _inner.ConvertPathFromInternal(systemPath);

    public string ConvertPathToInternal(FilePath path) => _inner.ConvertPathToInternal(path);

    public bool FileExists(FilePath path) => _inner.FileExists(path);

    public bool DirectoryExists(FilePath path) => _inner.DirectoryExists(path);

    public Stream OpenFile(FilePath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (access != FileAccess.Read || mode is FileMode.Create or FileMode.CreateNew or FileMode.Append or FileMode.Truncate)
            throw new UnauthorizedAccessException($"The filesystem is read-only: '{path}' cannot be opened for writing.");
        return _inner.OpenFile(path, mode, access, share);
    }

    public void CreateDirectory(FilePath path)
        => throw new UnauthorizedAccessException($"The filesystem is read-only: directory '{path}' cannot be created.");

    public void DeleteFile(FilePath path)
        => throw new UnauthorizedAccessException($"The filesystem is read-only: '{path}' cannot be deleted.");

    public void MoveFile(FilePath source, FilePath destination, bool overwrite = false)
        => throw new UnauthorizedAccessException(
            $"The filesystem is read-only: '{source}' cannot be moved to '{destination}'.");

    public IEnumerable<FilePath> EnumerateFiles(FilePath directory, string pattern = "*", bool recursive = false)
        => _inner.EnumerateFiles(directory, pattern, recursive);

    public IEnumerable<FilePath> EnumerateDirectories(FilePath directory, string pattern = "*", bool recursive = false)
        => _inner.EnumerateDirectories(directory, pattern, recursive);

    public DateTime GetLastWriteTime(FilePath path) => _inner.GetLastWriteTime(path);

    public bool CanWatch(FilePath directory) => _inner.CanWatch(directory);

    public IFileWatcher Watch(FilePath directory) => _inner.Watch(directory);

    /// <summary>This view does not own the wrapped filesystem, so disposal is a no-op.</summary>
    public void Dispose() { }
}
