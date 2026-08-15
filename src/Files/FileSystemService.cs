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
/// A path handed to the service is either:
/// <list type="bullet">
/// <item>an <b>operating-system path</b> (rooted — drive letter, UNC, or a leading
/// separator), mapped into the filesystem via <see cref="IFileSystem.ConvertPathFromInternal"/>;
/// or</item>
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

    /// <summary>The underlying Zio filesystem. All I/O funnels through this.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>Physical directory logical (non-rooted) paths resolve against.</summary>
    public string ContentRoot { get; }

    /// <summary>Logical mount points (e.g. <c>/Game</c>) mapped to physical directories.</summary>
    public IReadOnlyDictionary<UPath, string> Mounts { get; }

    public FileSystemService(IFileSystem fileSystem, string contentRoot, IReadOnlyDictionary<UPath, string>? mounts = null)
    {
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ContentRoot = string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot;
        Mounts = mounts ?? new Dictionary<UPath, string>();
    }

    /// <summary>Creates a service backed by the physical disk, rooted at <paramref name="contentRoot"/>.</summary>
    public static FileSystemService CreatePhysical(string contentRoot, IReadOnlyDictionary<UPath, string>? mounts = null)
        => new(new PhysicalFileSystem(), contentRoot, mounts);

    /// <summary>Creates a service backed by an empty in-memory filesystem (for tests and overlays).</summary>
    public static FileSystemService CreateMemory() => new(new MemoryFileSystem(), "/");

    // ------------------------------------------------------------------
    // Path bridge
    // ------------------------------------------------------------------

    /// <summary>True when <paramref name="path"/> is an operating-system path (drive letter, UNC, rooted).</summary>
    public static bool IsRooted(string path) => Path.IsPathRooted(path);

    /// <summary>Converts a user path (OS or logical) into a filesystem <see cref="UPath"/>.</summary>
    public UPath ToUPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
            return FileSystem.ConvertPathFromInternal(Path.GetFullPath(path));

        var logical = new UPath(path).ToAbsolute();
        foreach (var (mountPoint, targetDirectory) in Mounts)
        {
            if (logical == mountPoint || logical.IsInDirectory(mountPoint, recursive: true))
            {
                var relative = logical == mountPoint
                    ? UPath.Empty
                    : new UPath(logical.FullName[(mountPoint.FullName.Length + 1)..]);
                return ToDirectory(targetDirectory) / relative;
            }
        }

        return ToDirectory(ContentRoot) / logical.ToRelative();
    }

    /// <summary>Converts a user path the way <c>System.IO</c> would (relative paths resolve against the process working directory).</summary>
    public UPath ToWorkingDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FileSystem.ConvertPathFromInternal(Path.GetFullPath(path));
    }

    private UPath ToDirectory(string directory) => FileSystem.ConvertPathFromInternal(Path.GetFullPath(directory));

    /// <summary>Maps a filesystem path back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(UPath path) => FileSystem.ConvertPathToInternal(path);

    /// <summary>Maps a user path back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(string path) => ToSystemPath(ToUPath(path));

    /// <summary>Resolves a physical directory, returning its OS path when it exists (null otherwise).</summary>
    public string? ResolveSystemDirectory(string path)
    {
        var upath = ToUPath(path);
        return FileSystem.DirectoryExists(upath) ? ToSystemPath(upath) : null;
    }

    // ------------------------------------------------------------------
    // I/O
    // ------------------------------------------------------------------

    public string ReadAllText(string path) => FileSystem.ReadAllText(ToUPath(path));

    public string ReadAllText(UPath path) => FileSystem.ReadAllText(path);

    public byte[] ReadAllBytes(string path) => FileSystem.ReadAllBytes(ToUPath(path));

    public byte[] ReadAllBytes(UPath path) => FileSystem.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var upath = ToUPath(path);
        EnsureDirectory(upath.GetDirectory());
        FileSystem.WriteAllBytes(upath, content);
    }

    /// <summary>Opens a file for reading with shared write access (so editors and other tools can overwrite it while it is open).</summary>
    public Stream OpenRead(string path) => OpenRead(ToUPath(path));

    public Stream OpenRead(UPath path) => FileSystem.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public bool FileExists(string path) => FileSystem.FileExists(ToUPath(path));

    public bool FileExists(UPath path) => FileSystem.FileExists(path);

    public bool DirectoryExists(string path) => FileSystem.DirectoryExists(ToUPath(path));

    public bool DirectoryExists(UPath path) => FileSystem.DirectoryExists(path);

    /// <summary>Enumerates files under a directory, optionally recursively.</summary>
    public IEnumerable<UPath> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
        => FileSystem.EnumerateFiles(ToUPath(directory), pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public IEnumerable<UPath> EnumerateFiles(UPath directory, string pattern = "*", bool recursive = false)
        => FileSystem.EnumerateFiles(directory, pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public DateTime GetLastWriteTimeUtc(string path) => FileSystem.GetLastWriteTime(ToUPath(path));

    public DateTime GetLastWriteTimeUtc(UPath path) => FileSystem.GetLastWriteTime(path);

    public void CreateDirectory(string path) => FileSystem.CreateDirectory(ToUPath(path));

    public void DeleteFile(string path) => FileSystem.DeleteFile(ToUPath(path));

    public void DeleteFile(UPath path) => FileSystem.DeleteFile(path);

    // ------------------------------------------------------------------
    // Watching
    // ------------------------------------------------------------------

    /// <summary>True when the directory can be watched on the underlying filesystem.</summary>
    public bool CanWatch(string directory) => FileSystem.CanWatch(ToUPath(directory));

    public bool CanWatch(UPath directory) => FileSystem.CanWatch(directory);

    /// <summary>Returns a watcher for the directory (configure it before enabling events).</summary>
    public IFileSystemWatcher Watch(string directory) => FileSystem.Watch(ToUPath(directory));

    public IFileSystemWatcher Watch(UPath directory) => FileSystem.Watch(directory);

    private void EnsureDirectory(UPath directory)
    {
        if (!directory.IsNull && !FileSystem.DirectoryExists(directory))
            FileSystem.CreateDirectory(directory);
    }
}
