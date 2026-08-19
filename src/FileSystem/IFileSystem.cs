using System.IO;

namespace Crowbar.FileSystems;

/// <summary>
/// The filesystem contract the engine and editor code against. A backend is a
/// coordinate space of <see cref="FilePath"/>s plus the primitive operations on
/// it (existence, enumeration, open/read/write, watch). The physical-disk
/// implementation lives in the internal <c>ZioFileSystem</c> adapter; a host or test provides
/// whichever backend it needs (disk, memory, archive, ...). Nothing in this
/// project creates a backend — backends are supplied to
/// <see cref="FileSystemService"/> at startup.
/// </summary>
internal interface IFileSystem : IDisposable
{
    /// <summary>Maps an operating-system path into this filesystem's coordinate space.</summary>
    FilePath ConvertPathFromInternal(string systemPath);

    /// <summary>Maps a filesystem path back to an operating-system path.</summary>
    string ConvertPathToInternal(FilePath path);

    bool FileExists(FilePath path);

    bool DirectoryExists(FilePath path);

    /// <summary>Opens a file with the requested mode, access and sharing.</summary>
    Stream OpenFile(FilePath path, FileMode mode, FileAccess access, FileShare share);

    void CreateDirectory(FilePath path);

    void DeleteFile(FilePath path);

    /// <summary>Moves a file to a new path, replacing the destination when requested.</summary>
    void MoveFile(FilePath source, FilePath destination, bool overwrite = false);

    IEnumerable<FilePath> EnumerateFiles(FilePath directory, string pattern = "*", bool recursive = false);

    /// <summary>The file's last write time in UTC.</summary>
    DateTime GetLastWriteTime(FilePath path);

    /// <summary>True when the directory can be watched on this backend.</summary>
    bool CanWatch(FilePath directory);

    /// <summary>Returns a watcher for the directory (configure it before enabling events).</summary>
    IFileWatcher Watch(FilePath directory);
}
