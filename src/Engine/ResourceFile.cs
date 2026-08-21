using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// The base of every resource in the engine. Two families inherit it:
/// file <em>documents</em> (<see cref="LevelFile"/>, <see cref="CrowbarProjectFile"/>)
/// that parse their <see cref="Data"/> through <see cref="Load"/>, and loaded
/// <em>assets</em> (<see cref="Model"/>, <see cref="Texture2D"/>,
/// <see cref="Audio.AudioClip"/>, <see cref="Shader"/>) imported by
/// <see cref="Global.ResourceLibrary"/> and shared by their <see cref="Path"/>
/// through the engine's global cache. Custom game resources inherit it and
/// register their loader with <see cref="Global.ResourceLibrary.RegisterLoader{T}"/>
/// to become cacheable too.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AssetTypeAttribute(string extension) : Attribute
{
    public string Extension { get; } = extension;
}

public abstract class ResourceFile : IValid, IDisposable
{
    public string Path { get; internal set; } = string.Empty;
    public Stream? Data { get; private set; }
    public bool IsValid { get; protected set; }

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