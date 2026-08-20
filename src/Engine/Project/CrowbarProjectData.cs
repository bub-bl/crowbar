namespace Crowbar.Engine;

/// <summary>
/// The on-disk container of a Crowbar project, serialized as JSON (the
/// <c>.crproj</c> asset, see <see cref="CrowbarProjectSerializer"/>). It is
/// deliberately a plain data DTO — never the editor's runtime state — so the
/// file is stable, human-readable and diff-friendly in version control.
///
/// The structure is versioned by <see cref="Format"/>
/// (<see cref="CrowbarProjectFile.CurrentFormat"/>); <see cref="Id"/> is the
/// stable identity of the project, preserved across save/load, so external
/// references (a launcher, a build script) survive a round trip. The file sits
/// at the root of the project directory it describes — its directory is the
/// project's working root, and the rest of the project's content (levels,
/// gamemode scripts, assets) lives next to it.
/// </summary>
public sealed class CrowbarProjectData
{
    /// <summary>The file format version this payload uses.</summary>
    public int Format { get; init; } = CrowbarProjectFile.CurrentFormat;

    /// <summary>The stable identity of the project, preserved across save/load.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The display name of the project (shown in the editor title bar).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The project's own version (product version, not the editor's).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The project author, or null when unknown.</summary>
    public string? Author { get; init; }

    /// <summary>A short description of the project, or null when none is set.</summary>
    public string? Description { get; init; }
}