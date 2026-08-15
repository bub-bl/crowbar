using Crowbar.Files;

namespace Crowbar.Files.Zio;

/// <summary>Adapts a Zio watcher to Crowbar's <see cref="IFileWatcher"/>.</summary>
internal sealed class ZioFileWatcher : IFileWatcher
{
    private readonly global::Zio.IFileSystemWatcher _watcher;

    public ZioFileWatcher(global::Zio.IFileSystemWatcher watcher)
    {
        _watcher = watcher;
        _watcher.Changed += (_, e) => Changed?.Invoke(this, ToChanged(e));
        _watcher.Created += (_, e) => Created?.Invoke(this, ToChanged(e));
        _watcher.Deleted += (_, e) => Deleted?.Invoke(this, ToChanged(e));
        _watcher.Renamed += (_, e) => Renamed?.Invoke(this, ToRenamed(e));
    }

    public event EventHandler<FileChangedEventArgs>? Changed;
    public event EventHandler<FileChangedEventArgs>? Created;
    public event EventHandler<FileChangedEventArgs>? Deleted;
    public event EventHandler<FileRenamedEventArgs>? Renamed;

    public bool EnableRaisingEvents
    {
        get => _watcher.EnableRaisingEvents;
        set => _watcher.EnableRaisingEvents = value;
    }

    public string Filter
    {
        get => _watcher.Filter;
        set => _watcher.Filter = value;
    }

    public bool IncludeSubdirectories
    {
        get => _watcher.IncludeSubdirectories;
        set => _watcher.IncludeSubdirectories = value;
    }

    public FileChangeFilters NotifyFilter
    {
        get => (FileChangeFilters)(int)_watcher.NotifyFilter;
        set => _watcher.NotifyFilter = (global::Zio.NotifyFilters)(int)value;
    }

    public void Dispose() => _watcher.Dispose();

    private static FileChangedEventArgs ToChanged(global::Zio.FileChangedEventArgs e)
        => new(ToChangeType(e.ChangeType), new FilePath(e.FullPath.FullName), e.Name);

    private static FileRenamedEventArgs ToRenamed(global::Zio.FileRenamedEventArgs e)
        => new(ToChangeType(e.ChangeType), new FilePath(e.FullPath.FullName), e.Name, new FilePath(e.OldFullPath.FullName), e.OldName);

    private static FileChangeType ToChangeType(global::Zio.WatcherChangeTypes changeType) => changeType switch
    {
        global::Zio.WatcherChangeTypes.Created => FileChangeType.Created,
        global::Zio.WatcherChangeTypes.Deleted => FileChangeType.Deleted,
        global::Zio.WatcherChangeTypes.Renamed => FileChangeType.Renamed,
        _ => FileChangeType.Changed
    };
}
