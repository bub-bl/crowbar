using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// The base of every resource in the engine. Two families inherit it:
/// file <em>documents</em> (<see cref="LevelFile"/>, <see cref="CrowbarProjectFile"/>)
/// that parse their <see cref="Data"/> through <see cref="Load"/>, and loaded
/// <em>assets</em> (<see cref="Model"/>, <see cref="Texture2D"/>,
/// <see cref="Audio.AudioClip"/>, <see cref="Shader"/>) that override
/// <see cref="Load"/> with their importer and are shared by their
/// <see cref="Path"/> through the engine's global cache. Both families follow
/// the same model: the instance is allocated, its <see cref="Path"/> is
/// assigned, then <see cref="Load"/> populates it and <see cref="Unload"/>
/// releases what it owns. Custom game resources mark themselves
/// <see cref="AssetTypeAttribute"/> to become cacheable too.
/// </summary>
/// <summary>
/// Marks a <see cref="ResourceFile"/> subclass as a file-backed type and
/// declares the file extensions it loads. Registering an assembly discovers
/// every marked type: the library allocates its instances, assigns their
/// <see cref="ResourceFile.Path"/> and populates them through the
/// <see cref="ResourceFile.Load"/> override, then shares them by path through
/// <see cref="Global.ResourceLibrary"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AssetTypeAttribute(params string[] extensions) : Attribute
{
    /// <summary>The file extensions this type loads, without the leading dot (e.g. "gltf").</summary>
    public IReadOnlyList<string> Extensions { get; } = extensions;
}

public abstract class ResourceFile : IValid, IDisposable
{
    public string Path { get; internal set; } = string.Empty;
    public Stream? Data { get; private set; }
    public bool IsValid { get; protected set; }

    /// <summary>
    /// Populates this instance from <see cref="Path"/>. The base opens the raw
    /// content stream for documents that parse <see cref="Data"/>; file-backed
    /// assets (<see cref="Model"/>, <see cref="Shader"/>, ...) override it with
    /// their importer. The library calls it right after allocating the instance
    /// and assigning <see cref="Path"/>.
    /// </summary>
    public virtual void Load()
    {
        if (FileSystem.Content.FileExists(Path))
        {
            // Opened with shared write access so an external tool (or the editor
            // itself) can overwrite the asset while it is loaded.
            Data = FileSystem.Content.OpenRead(Path);
            IsValid = true;
        }
        else
        {
            IsValid = false;
            throw new FileNotFoundException("The file does not exist.", Path);
        }
    }

    public virtual void Unload()
    {
        Data?.Dispose();
        Data = null;
        IsValid = false;
    }

    public void Dispose()
    {
        Unload();
        GC.SuppressFinalize(this);
    }
}