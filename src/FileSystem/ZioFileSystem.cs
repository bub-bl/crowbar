using System.IO;
using Zio;

namespace Crowbar.FileSystems;

/// <summary>
/// Adapts a Zio filesystem to Crowbar's <see cref="IFileSystem"/>. This is the
/// only place the Zio library is referenced (and the only place a concrete
/// filesystem is created): every other project codes against Crowbar.FileSystems's
/// own contracts and supplies this backend through <see cref="FileSystem.Configure"/>
/// at startup. Internal so the Zio backend never leaks into the public API.
/// </summary>
internal sealed class ZioFileSystem : IFileSystem
{
    private readonly global::Zio.IFileSystem _inner;

    private ZioFileSystem(global::Zio.IFileSystem inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>A physical-disk backend.</summary>
    public static ZioFileSystem Physical() => new(new global::Zio.FileSystems.PhysicalFileSystem());

    /// <summary>An empty in-memory backend (for tests and session overlays).</summary>
    public static ZioFileSystem Memory() => new(new global::Zio.FileSystems.MemoryFileSystem());

    public FilePath ConvertPathFromInternal(string systemPath)
        => new(_inner.ConvertPathFromInternal(systemPath).FullName);

    public string ConvertPathToInternal(FilePath path)
        => _inner.ConvertPathToInternal(ToUPath(path));

    public bool FileExists(FilePath path) => _inner.FileExists(ToUPath(path));

    public bool DirectoryExists(FilePath path) => _inner.DirectoryExists(ToUPath(path));

    public Stream OpenFile(FilePath path, FileMode mode, FileAccess access, FileShare share)
        => _inner.OpenFile(ToUPath(path), mode, access, share);

    public void CreateDirectory(FilePath path) => _inner.CreateDirectory(ToUPath(path));

    public void DeleteFile(FilePath path) => _inner.DeleteFile(ToUPath(path));

    public void MoveFile(FilePath source, FilePath destination, bool overwrite = false)
    {
        // Zio's MoveFile has no overwrite flag: replace the destination first
        // so a temp-file save can land atomically over an existing asset.
        if (overwrite && _inner.FileExists(ToUPath(destination)))
            _inner.DeleteFile(ToUPath(destination));
        _inner.MoveFile(ToUPath(source), ToUPath(destination));
    }

    public IEnumerable<FilePath> EnumerateFiles(FilePath directory, string pattern = "*", bool recursive = false)
        => _inner.EnumerateFiles(
                ToUPath(directory),
                pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(path => new FilePath(path.FullName));

    public IEnumerable<FilePath> EnumerateDirectories(FilePath directory, string pattern = "*", bool recursive = false)
        => _inner.EnumerateDirectories(
                ToUPath(directory),
                pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(path => new FilePath(path.FullName));

    public DateTime GetLastWriteTime(FilePath path) => _inner.GetLastWriteTime(ToUPath(path));

    public bool CanWatch(FilePath directory) => _inner.CanWatch(ToUPath(directory));

    public IFileWatcher Watch(FilePath directory) => new ZioFileWatcher(_inner.Watch(ToUPath(directory)));

    public void Dispose() => _inner.Dispose();

    private static global::Zio.UPath ToUPath(FilePath path) => new(path.FullName);
}
