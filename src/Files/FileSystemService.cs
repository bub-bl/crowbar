using System.IO;

namespace Crowbar.Files;

/// <summary>
/// The engine's and editor's single point of access to the filesystem. Every
/// content read, write, enumeration and watch goes through this service, so the
/// storage backing the content (physical disk, in-memory, zip archive, ...) can
/// be swapped without touching the call sites.
///
/// This class is the public boundary of Crowbar.Files: its surface only uses
/// Crowbar.Files' own types (<see cref="FilePath"/>, <see cref="IFileWatcher"/>,
/// <see cref="IFileSystem"/>) and the BCL, so consumers never reference a
/// concrete backend. There is no dependency injection — the engine and editor
/// reach the service through the static <see cref="FileSystem.Content"/>, which
/// the host assigns once at startup (see <see cref="IFileSystem"/> for how a
/// backend is supplied).
///
/// A path handed to the service is either:
/// <list type="bullet">
/// <item>an <b>operating-system path</b> (rooted — drive letter, UNC, or a leading
/// separator), mapped into the backend; or</item>
/// <item>a <b>logical path</b>, resolved against <see cref="ContentRoot"/> and the
/// configured <see cref="Mounts"/> (logical prefix → physical directory).</item>
/// </list>
///
/// Logical paths let the editor and a packaged build address the same content
/// (<c>Shaders/Pbr.wgsl</c>, <c>Game/DemoGamemode.cs</c>, ...) even though the
/// physical layout differs.
/// </summary>
public sealed class FileSystemService
{
    /// <summary>The backend filesystem all I/O is delegated to.</summary>
    public IFileSystem Backend { get; }

    /// <summary>Physical directory non-rooted logical paths resolve against.</summary>
    public string ContentRoot { get; }

    /// <summary>Logical mount points (e.g. <c>/Game</c>) mapped to physical directories.</summary>
    public IReadOnlyDictionary<FilePath, string> Mounts { get; }

    /// <summary>
    /// Composes the service over a backend. This is called once by the host's
    /// startup code (and by tests); consumers never construct it themselves.
    /// </summary>
    public FileSystemService(IFileSystem backend, string contentRoot, IReadOnlyDictionary<FilePath, string>? mounts = null)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ContentRoot = string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot;
        Mounts = mounts ?? new Dictionary<FilePath, string>();
    }

    // ------------------------------------------------------------------
    // Path bridge
    // ------------------------------------------------------------------

    /// <summary>True when <paramref name="path"/> is an operating-system path (drive letter, UNC, rooted).</summary>
    public static bool IsRooted(string path) => Path.IsPathRooted(path);

    /// <summary>Converts a user path (OS or logical) into a backend <see cref="FilePath"/>.</summary>
    public FilePath ToFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
            return Backend.ConvertPathFromInternal(Path.GetFullPath(path));

        var logical = new FilePath(path).ToAbsolute();
        foreach (var (mountPoint, targetDirectory) in Mounts)
        {
            if (logical == mountPoint || logical.IsInDirectory(mountPoint, recursive: true))
            {
                var relative = logical == mountPoint
                    ? FilePath.Empty
                    : new FilePath(logical.FullName[(mountPoint.FullName.Length + 1)..]);
                return ToDirectoryPath(targetDirectory) / relative;
            }
        }

        return ToDirectoryPath(ContentRoot) / logical.ToRelative();
    }

    /// <summary>Converts a user path the way <c>System.IO</c> would (relative paths resolve against the process working directory).</summary>
    public FilePath ToWorkingDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Backend.ConvertPathFromInternal(Path.GetFullPath(path));
    }

    /// <summary>Maps a backend path back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(FilePath path) => Backend.ConvertPathToInternal(path);

    /// <summary>Maps a user path back to an operating-system path (native/process interop).</summary>
    public string ToSystemPath(string path) => ToSystemPath(ToFilePath(path));

    /// <summary>Resolves a physical directory, returning its OS path when it exists (null otherwise).</summary>
    public string? ResolveSystemDirectory(string path)
    {
        var filePath = ToFilePath(path);
        return Backend.DirectoryExists(filePath) ? ToSystemPath(filePath) : null;
    }

    private FilePath ToDirectoryPath(string directory)
        => Backend.ConvertPathFromInternal(Path.GetFullPath(directory));

    // ------------------------------------------------------------------
    // I/O
    // ------------------------------------------------------------------

    public string ReadAllText(string path) => Backend.ReadAllText(ToFilePath(path));

    public string ReadAllText(FilePath path) => Backend.ReadAllText(path);

    public string[] ReadAllLines(string path) => Backend.ReadAllLines(ToFilePath(path));

    public string[] ReadAllLines(FilePath path) => Backend.ReadAllLines(path);

    public byte[] ReadAllBytes(string path) => Backend.ReadAllBytes(ToFilePath(path));

    public byte[] ReadAllBytes(FilePath path) => Backend.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] content) => Backend.WriteAllBytes(ToFilePath(path), content);

    public Stream OpenRead(string path) => Backend.OpenRead(ToFilePath(path));

    public Stream OpenRead(FilePath path) => Backend.OpenRead(path);

    public bool FileExists(string path) => Backend.FileExists(ToFilePath(path));

    public bool FileExists(FilePath path) => Backend.FileExists(path);

    public bool DirectoryExists(string path) => Backend.DirectoryExists(ToFilePath(path));

    public bool DirectoryExists(FilePath path) => Backend.DirectoryExists(path);

    public IEnumerable<FilePath> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
        => Backend.EnumerateFiles(ToFilePath(directory), pattern, recursive);

    public IEnumerable<FilePath> EnumerateFiles(FilePath directory, string pattern = "*", bool recursive = false)
        => Backend.EnumerateFiles(directory, pattern, recursive);

    public DateTime GetLastWriteTimeUtc(string path) => Backend.GetLastWriteTime(ToFilePath(path));

    public DateTime GetLastWriteTimeUtc(FilePath path) => Backend.GetLastWriteTime(path);

    public void CreateDirectory(string path) => Backend.CreateDirectory(ToFilePath(path));

    public void DeleteFile(string path) => Backend.DeleteFile(ToFilePath(path));

    public void DeleteFile(FilePath path) => Backend.DeleteFile(path);

    public bool CanWatch(string directory) => Backend.CanWatch(ToFilePath(directory));

    public bool CanWatch(FilePath directory) => Backend.CanWatch(directory);

    public IFileWatcher Watch(string directory) => Backend.Watch(ToFilePath(directory));

    public IFileWatcher Watch(FilePath directory) => Backend.Watch(directory);
}
