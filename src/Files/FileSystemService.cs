using System.IO;
using Zio;
using Zio.FileSystems;

namespace Crowbar.Files;

/// <summary>
/// The engine's and editor's single point of access to the filesystem, built on
/// Zio's <see cref="IFileSystem"/>. Every content read, write, enumeration and
/// watch goes through this service, so the storage backing the content (physical
/// disk, in-memory, zip archive, ...) can be swapped without touching the call
/// sites.
///
/// This class is the public boundary of Crowbar.Files: its surface only uses
/// Crowbar.Files' own types (<see cref="FilePath"/>, <see cref="IFileWatcher"/>)
/// and the BCL, so consumers never reference the underlying filesystem library.
///
/// A path handed to the service is either:
/// <list type="bullet">
/// <item>an <b>operating-system path</b> (rooted — drive letter, UNC, or a leading
/// separator), mapped into the filesystem; or</item>
/// <item>a <b>logical path</b>, resolved against <see cref="ContentRoot"/> and the
/// configured <see cref="Mounts"/> (logical prefix → physical directory).</item>
/// </list>
///
/// Logical paths let the editor and a packaged build address the same content
/// (<c>Shaders/Pbr.wgsl</c>, <c>Game/DemoGamemode.cs</c>, ...) even though the
/// physical layout differs. <see cref="Default"/> is the process-wide service;
/// hosts (and tests) may replace it or construct their own.
/// </summary>
public sealed class FileSystemService
{
    /// <summary>The process-wide service used by the engine and editor by default.</summary>
    public static FileSystemService Default { get; set; } = CreatePhysical(AppContext.BaseDirectory);

    internal IFileSystem FileSystem { get; }

    /// <summary>Physical directory logical (non-rooted) paths resolve against.</summary>
    public string ContentRoot { get; }

    /// <summary>Logical mount points (e.g. <c>/Game</c>) mapped to physical directories.</summary>
    public IReadOnlyDictionary<FilePath, string> Mounts { get; }

    internal FileSystemService(IFileSystem fileSystem, string contentRoot, IReadOnlyDictionary<FilePath, string>? mounts = null)
    {
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ContentRoot = string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot;
        Mounts = mounts ?? new Dictionary<FilePath, string>();
    }

    /// <summary>Creates a service backed by the physical disk, rooted at <paramref name="contentRoot"/>.</summary>
    public static FileSystemService CreatePhysical(string contentRoot, IReadOnlyDictionary<FilePath, string>? mounts = null)
        => new(new PhysicalFileSystem(), contentRoot, mounts);

    /// <summary>Creates a service backed by an empty in-memory filesystem (for tests and overlays).</summary>
    public static FileSystemService CreateMemory() => new(new MemoryFileSystem(), "/");

    // ------------------------------------------------------------------
    // Path bridge
    // ------------------------------------------------------------------

    /// <summary>True when <paramref name="path"/> is an operating-system path (drive letter, UNC, rooted).</summary>
    public static bool IsRooted(string path) => Path.IsPathRooted(path);

    /// <summary>Converts a user path (OS or logical) into a <see cref="FilePath"/>.</summary>
    public FilePath ToFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
            return new FilePath(FileSystem.ConvertPathFromInternal(Path.GetFullPath(path)));

        var logical = new UPath(path).ToAbsolute();
        foreach (var (mountPoint, targetDirectory) in Mounts)
        {
            var mount = mountPoint.Path;
            if (logical == mount || logical.IsInDirectory(mount, recursive: true))
            {
                var relative = logical == mount
                    ? UPath.Empty
                    : new UPath(logical.FullName[(mount.FullName.Length + 1)..]);
                return new FilePath(ToDirectory(targetDirectory) / relative);
            }
        }

        return new FilePath(ToDirectory(ContentRoot) / logical.ToRelative());
    }

    /// <summary>Converts a user path the way <c>System.IO</c> would (relative paths resolve against the process working directory).</summary>
    public FilePath ToWorkingDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FilePath(FileSystem.ConvertPathFromInternal(Path.GetFullPath(path)));
    }

    private UPath ToDirectory(string directory) => FileSystem.ConvertPathFromInternal(Path.GetFullPath(directory));

    /// <summary>Maps a <see cref="FilePath"/> back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(FilePath path) => FileSystem.ConvertPathToInternal(path.Path);

    /// <summary>Maps a user path back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(string path) => ToSystemPath(ToFilePath(path));

    /// <summary>Resolves a physical directory, returning its OS path when it exists (null otherwise).</summary>
    public string? ResolveSystemDirectory(string path)
    {
        var filePath = ToFilePath(path);
        return FileSystem.DirectoryExists(filePath.Path) ? ToSystemPath(filePath) : null;
    }

    // ------------------------------------------------------------------
    // I/O
    // ------------------------------------------------------------------

    public string ReadAllText(string path) => FileSystem.ReadAllText(ToFilePath(path).Path);

    public string ReadAllText(FilePath path) => FileSystem.ReadAllText(path.Path);

    public string[] ReadAllLines(string path) => FileSystem.ReadAllLines(ToFilePath(path).Path);

    public string[] ReadAllLines(FilePath path) => FileSystem.ReadAllLines(path.Path);

    public byte[] ReadAllBytes(string path) => FileSystem.ReadAllBytes(ToFilePath(path).Path);

    public byte[] ReadAllBytes(FilePath path) => FileSystem.ReadAllBytes(path.Path);

    public void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var filePath = ToFilePath(path);
        EnsureDirectory(filePath.GetDirectory());
        FileSystem.WriteAllBytes(filePath.Path, content);
    }

    /// <summary>Opens a file for reading with shared write access (so editors and other tools can overwrite it while it is open).</summary>
    public Stream OpenRead(string path) => OpenRead(ToFilePath(path));

    public Stream OpenRead(FilePath path) => FileSystem.OpenFile(path.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public bool FileExists(string path) => FileSystem.FileExists(ToFilePath(path).Path);

    public bool FileExists(FilePath path) => FileSystem.FileExists(path.Path);

    public bool DirectoryExists(string path) => FileSystem.DirectoryExists(ToFilePath(path).Path);

    public bool DirectoryExists(FilePath path) => FileSystem.DirectoryExists(path.Path);

    /// <summary>Enumerates files under a directory, optionally recursively.</summary>
    public IEnumerable<FilePath> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
        => EnumerateFiles(ToFilePath(directory), pattern, recursive);

    public IEnumerable<FilePath> EnumerateFiles(FilePath directory, string pattern = "*", bool recursive = false)
        => FileSystem.EnumerateFiles(directory.Path, pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(path => new FilePath(path));

    public DateTime GetLastWriteTimeUtc(string path) => FileSystem.GetLastWriteTime(ToFilePath(path).Path);

    public DateTime GetLastWriteTimeUtc(FilePath path) => FileSystem.GetLastWriteTime(path.Path);

    public void CreateDirectory(string path) => FileSystem.CreateDirectory(ToFilePath(path).Path);

    public void DeleteFile(string path) => FileSystem.DeleteFile(ToFilePath(path).Path);

    public void DeleteFile(FilePath path) => FileSystem.DeleteFile(path.Path);

    // ------------------------------------------------------------------
    // Watching
    // ------------------------------------------------------------------

    /// <summary>True when the directory can be watched on the underlying filesystem.</summary>
    public bool CanWatch(string directory) => CanWatch(ToFilePath(directory));

    public bool CanWatch(FilePath directory) => FileSystem.CanWatch(directory.Path);

    /// <summary>Returns a watcher for the directory (configure it before enabling events).</summary>
    public IFileWatcher Watch(string directory) => Watch(ToFilePath(directory));

    public IFileWatcher Watch(FilePath directory) => new ZioFileWatcher(FileSystem.Watch(directory.Path));

    private void EnsureDirectory(FilePath directory)
    {
        if (!directory.IsNull && !FileSystem.DirectoryExists(directory.Path))
            FileSystem.CreateDirectory(directory.Path);
    }
}
