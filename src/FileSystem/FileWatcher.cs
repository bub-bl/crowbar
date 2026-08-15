namespace Crowbar.FileSystems;

/// <summary>The kind of filesystem change reported by <see cref="IFileWatcher"/>.</summary>
public enum FileChangeType
{
    Created,
    Deleted,
    Changed,
    Renamed
}

/// <summary>Which changes a watcher reports (mirrors the operating-system watch filters).</summary>
[Flags]
public enum FileChangeFilters
{
    None = 0,
    FileName = 1,
    DirectoryName = 2,
    Attributes = 4,
    Size = 8,
    LastWrite = 16,
    LastAccess = 32,
    CreationTime = 64,
    Security = 256,
    Default = FileName | DirectoryName | LastWrite
}

/// <summary>Base event arguments for a file or directory change.</summary>
public class FileChangedEventArgs : EventArgs
{
    public FileChangedEventArgs(FileChangeType changeType, FilePath fullPath, string name)
    {
        ChangeType = changeType;
        FullPath = fullPath;
        Name = name;
    }

    /// <summary>The kind of change that occurred.</summary>
    public FileChangeType ChangeType { get; }

    /// <summary>The path of the affected file or directory.</summary>
    public FilePath FullPath { get; }

    /// <summary>The name of the affected file or directory.</summary>
    public string Name { get; }
}

/// <summary>Event arguments for a file or directory rename.</summary>
public sealed class FileRenamedEventArgs : FileChangedEventArgs
{
    public FileRenamedEventArgs(FileChangeType changeType, FilePath fullPath, string name,
        FilePath oldFullPath, string oldName)
        : base(changeType, fullPath, name)
    {
        OldFullPath = oldFullPath;
        OldName = oldName;
    }

    /// <summary>The previous location of the file or directory.</summary>
    public FilePath OldFullPath { get; }

    /// <summary>The previous name of the file or directory.</summary>
    public string OldName { get; }
}

/// <summary>
/// Watches a directory for changes. The backing implementation is provided by
/// the filesystem backend returned from <see cref="FileSystemService.Watch(FilePath)"/>.
/// </summary>
public interface IFileWatcher : IDisposable
{
    event EventHandler<FileChangedEventArgs>? Changed;
    event EventHandler<FileChangedEventArgs>? Created;
    event EventHandler<FileChangedEventArgs>? Deleted;
    event EventHandler<FileRenamedEventArgs>? Renamed;

    /// <summary>True to raise events, false to never raise them (default false).</summary>
    bool EnableRaisingEvents { get; set; }

    /// <summary>File name and extension filter (default <c>*.*</c>).</summary>
    string Filter { get; set; }

    /// <summary>True to watch all subdirectories, false for direct children only.</summary>
    bool IncludeSubdirectories { get; set; }

    /// <summary>Which changes to report.</summary>
    FileChangeFilters NotifyFilter { get; set; }
}
