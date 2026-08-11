namespace Crowbar.Engine.World;

public sealed record LevelFileMetadata(string Name, string Version);

/// <summary>
/// The serialized form of a <see cref="Level"/> (asset extension ".level").
/// The runtime counterpart is the <see cref="Level"/> class, which owns the
/// in-memory entities; this file is only the persistence container.
/// </summary>
[AssetType("level")]
public sealed class LevelFile : ResourceFile
{
    public Guid Id { get; init; }
    public required LevelFileMetadata Metadata { get; init; }
    // public List<Entity> Entities { get; private set; } = [];
}
