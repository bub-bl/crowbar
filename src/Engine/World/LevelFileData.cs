using System.Text.Json;

namespace Crowbar.Engine;

/// <summary>
/// The on-disk container of a <see cref="Level"/>, serialized as JSON (see
/// <see cref="LevelSerializer"/>). It is deliberately a plain data DTO — never
/// the runtime objects — so the file format is stable, diff-friendly and open
/// to forward compatibility: unknown components and properties are skipped on
/// load instead of failing.
///
/// The structure is versioned by <see cref="Format"/> (<see cref="LevelFile.CurrentFormat"/>);
/// every <see cref="Guid"/> (the level's and each entity's) is persisted so
/// external references survive a save/load round trip. <see cref="Entities"/>
/// carry their own components; parent/child relationships between transforms
/// are resolved in a second pass from <see cref="Attachments"/>.
/// </summary>
public sealed class LevelFileData
{
    /// <summary>The file format version this payload uses.</summary>
    public int Format { get; init; } = LevelFile.CurrentFormat;

    /// <summary>The stable identity of the level, preserved across save/load.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name and editor version that last wrote the level.</summary>
    public LevelFileMetadata? Metadata { get; init; }

    /// <summary>The level's entities, in spawn order.</summary>
    public List<LevelEntityData> Entities { get; init; } = [];

    /// <summary>Parent → child transform relationships, resolved after all entities exist.</summary>
    public List<LevelAttachmentData> Attachments { get; init; } = [];
}

/// <summary>One entity of a saved level: stable id, name and its components.</summary>
public sealed class LevelEntityData
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public List<LevelComponentData> Components { get; init; } = [];
}

/// <summary>
/// One component of a saved entity. <see cref="Type"/> is the component's short
/// type name (resolved through <see cref="TypeRegistry"/> on load —
/// unknown names are skipped, which is the forward-compatibility contract).
/// <see cref="Transform"/> carries the local transform of spatial components
/// (TransformComponent-derived) in the canonical string format, and
/// <see cref="Properties"/> holds the writable <see cref="PropertyAttribute"/>
/// values as raw JSON, produced and consumed by System.Text.Json (each value is
/// a <see cref="JsonElement"/>; the converters registered by
/// <see cref="LevelSerializer"/> define how each property type is represented).
/// </summary>
public sealed class LevelComponentData
{
    public string Type { get; init; } = string.Empty;

    /// <summary>The local transform of a spatial component, or null for purely logical components.</summary>
    public string? Transform { get; set; }

    /// <summary>The writable [Property] values, as raw JSON elements.</summary>
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

/// <summary>A parent → child transform attachment between two entities of the saved level.</summary>
public sealed class LevelAttachmentData
{
    public Guid Parent { get; init; }

    public Guid Child { get; init; }
}
