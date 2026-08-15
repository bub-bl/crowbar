using Crowbar.Files;

namespace Crowbar.Engine;

[AttributeUsage(AttributeTargets.Class)]
public sealed class AssetTypeAttribute(string extension) : Attribute
{
    public string Extension { get; } = extension;
}

public abstract class ResourceFile : IValid, IDisposable
{
    public string Path { get; internal set; } = string.Empty;
    public Stream? Data { get; private set; }
    public bool IsValid { get; private set; }

    public void Load()
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

    public void Unload()
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