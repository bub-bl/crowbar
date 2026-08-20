using System.Text;
using System.Text.Json;
using Crowbar.FileSystems;

namespace Crowbar.Engine;

/// <summary>
/// The serialized form of a Crowbar project (asset extension ".crproj"). The
/// file is the project's identity card: it sits at the root of a project
/// directory and records its name, version, author and description. The
/// directory containing the file <em>is</em> the project — opening a
/// <c>.crproj</c> roots the project filesystem at that directory.
///
/// The container is the plain-data <see cref="CrowbarProjectData"/> DTO, written
/// by <see cref="CrowbarProjectSerializer"/> as versioned, pretty-printed JSON.
/// Because a <c>.crproj</c> must be openable before any filesystem is
/// configured (double-clicking the file launches the editor with it), the file
/// also supports disk-first loading: <see cref="LoadFromDisk"/> reads straight
/// from the operating system, while <see cref="Load"/>/<see cref="Save"/> go
/// through the configured project filesystem like <see cref="LevelFile"/>.
/// </summary>
[AssetType("crproj")]
public sealed class CrowbarProjectFile : ResourceFile
{
    /// <summary>
    /// The current on-disk format version (see <see cref="CrowbarProjectData.Format"/>).
    /// Bump it on any breaking layout change and add a migration path in
    /// <see cref="CrowbarProjectSerializer.Deserialize"/> for older files.
    /// </summary>
    public const int CurrentFormat = 1;

    /// <summary>The stable identity of the project, preserved across save/load.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>The display name of the project.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The project's own version (product version, not the editor's).</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>The project author, or null when unknown.</summary>
    public string? Author { get; private set; }

    /// <summary>A short description of the project, or null when none is set.</summary>
    public string? Description { get; private set; }

    /// <summary>The parsed content of the file (null until a load succeeds).</summary>
    public CrowbarProjectData? ProjectData { get; private set; }

    /// <summary>Loads and parses a project asset from the project filesystem.</summary>
    public static CrowbarProjectFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new CrowbarProjectFile { Path = path };
        file.Load();
        return file;
    }

    /// <summary>
    /// Loads a project file straight from the operating system, before any
    /// filesystem is configured. This is the startup path of a double-click
    /// launch: the editor receives the <c>.crproj</c> path as its first command
    /// line argument, reads it here, then roots the project filesystem at its
    /// directory.
    /// </summary>
    public static CrowbarProjectFile LoadFromDisk(string systemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPath);
        var fullPath = System.IO.Path.GetFullPath(systemPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The project file does not exist.", fullPath);

        var file = new CrowbarProjectFile { Path = fullPath };
        Apply(file, CrowbarProjectSerializer.Deserialize(File.ReadAllText(fullPath)));
        file.IsValid = true;
        return file;
    }

    /// <summary>
    /// Serializes the project data and writes it to the project filesystem. The
    /// write is atomic: the JSON goes to a sibling <c>.tmp</c> file first, then
    /// is moved over the destination, so a crash mid-write can never leave a
    /// truncated project behind.
    /// </summary>
    public static void Save(CrowbarProjectData data, string path)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fs = FileSystem.Project;
        var bytes = Encoding.UTF8.GetBytes(CrowbarProjectSerializer.Serialize(data));
        var tempPath = path + ".tmp";
        fs.WriteAllBytes(tempPath, bytes);
        fs.MoveFile(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Disk-first counterpart of <see cref="Save"/>: writes straight to the
    /// operating system, atomically, without any configured filesystem.
    /// </summary>
    public static void SaveToDisk(CrowbarProjectData data, string systemPath)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPath);

        var fullPath = System.IO.Path.GetFullPath(systemPath);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, CrowbarProjectSerializer.Serialize(data), Encoding.UTF8);
        File.Move(tempPath, fullPath, overwrite: true);
    }

    /// <summary>
    /// Parses the file's JSON into <see cref="ProjectData"/> and the exposed
    /// metadata. The project file lives at the root of the project directory, so
    /// it is addressed through the read-write project filesystem once that is
    /// configured (the <see cref="LoadFromDisk"/> path covers startup).
    /// </summary>
    public override void Load()
    {
        var fs = FileSystem.Project;
        if (!fs.FileExists(Path))
            throw new FileNotFoundException("The project file does not exist.", Path);

        Apply(this, CrowbarProjectSerializer.Deserialize(fs.ReadAllText(Path)));
        IsValid = true;
    }

    public override void Unload()
    {
        base.Unload();
        ProjectData = null;
    }

    private static void Apply(CrowbarProjectFile file, CrowbarProjectData data)
    {
        file.ProjectData = data;
        file.Id = data.Id;
        file.Name = data.Name;
        file.Version = data.Version;
        file.Author = data.Author;
        file.Description = data.Description;
    }
}