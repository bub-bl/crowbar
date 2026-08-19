using System.Text;

namespace Crowbar.Engine;

public sealed record LevelFileMetadata(string Name, string Version);

/// <summary>
/// The serialized form of a <see cref="Level"/> (asset extension ".level").
/// The runtime counterpart is the <see cref="Level"/> class, which owns the
/// in-memory entities; this file is only the persistence container. It is the
/// bridge between the two: <see cref="Save"/> writes a level to the read-write
/// project filesystem as versioned, pretty-printed JSON (through
/// <see cref="LevelSerializer"/>), <see cref="Load"/> parses a file back into
/// its <see cref="LevelFileData"/> DTO, and <see cref="CreateLevel"/>
/// materializes that DTO into a live level.
///
/// Levels are user-authored assets, so unlike read-only content resources they
/// live on <see cref="Crowbar.FileSystems.FileSystem.Project"/>.
/// </summary>
[AssetType("level")]
public sealed class LevelFile : ResourceFile
{
    /// <summary>
    /// The current on-disk format version (see <see cref="LevelFileData.Format"/>).
    /// Bump it on any breaking layout change and add a migration path in
    /// <see cref="LevelSerializer.Deserialize"/> for older files.
    /// </summary>
    public const int CurrentFormat = 1;

    /// <summary>The stable identity of the level, preserved across save/load.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>The display name and editor version recorded when the file was saved.</summary>
    public LevelFileMetadata? Metadata { get; private set; }

    /// <summary>The parsed content of the file (null until <see cref="Load"/> succeeds).</summary>
    public LevelFileData? LevelData { get; private set; }

    /// <summary>Loads and parses a level asset from the project filesystem.</summary>
    public static LevelFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new LevelFile { Path = path };
        file.Load();
        return file;
    }

    /// <summary>
    /// Serializes the level and writes it to the project filesystem. The write
    /// is atomic: the JSON goes to a sibling <c>.tmp</c> file first, then is
    /// moved over the destination, so a crash mid-write can never leave a
    /// truncated level behind.
    /// </summary>
    public static void Save(Level level, string path)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fs = Crowbar.FileSystems.FileSystem.Project;
        var bytes = Encoding.UTF8.GetBytes(LevelSerializer.Serialize(level));
        var tempPath = path + ".tmp";
        fs.WriteAllBytes(tempPath, bytes);
        fs.MoveFile(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Parses the file's JSON into <see cref="LevelData"/>, <see cref="Id"/> and
    /// <see cref="Metadata"/>. Levels live on the project filesystem (they are
    /// user-authored assets), unlike the read-only content resources handled by
    /// the base class.
    /// </summary>
    public override void Load()
    {
        var fs = Crowbar.FileSystems.FileSystem.Project;
        if (!fs.FileExists(Path))
            throw new FileNotFoundException("The level file does not exist.", Path);

        var data = LevelSerializer.Deserialize(fs.ReadAllText(Path));
        LevelData = data;
        Id = data.Id;
        Metadata = data.Metadata;
        IsValid = true;
    }

    public override void Unload()
    {
        base.Unload();
        LevelData = null;
        Metadata = null;
    }

    /// <summary>Materializes the parsed data into a live level inside <paramref name="world"/>.</summary>
    public Level CreateLevel(World world)
    {
        if (LevelData is null)
            throw new InvalidOperationException("The level file has not been loaded (call Load() first).");
        return LevelSerializer.CreateLevel(world, LevelData);
    }
}
