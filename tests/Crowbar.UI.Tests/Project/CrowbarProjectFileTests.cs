using System.Text.Json;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Tests;

public class CrowbarProjectFileTests
{
    private static CrowbarProjectData BuildProjectData() => new()
    {
        Id = new Guid("8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081"),
        Name = "MyGame",
        Version = "0.1",
        Author = "Jane Doe",
        Description = "Un jeu de démonstration Crowbar."
    };

    [Fact]
    public void RoundTrip_ThroughSerializer_PreservesFields()
    {
        var source = BuildProjectData();

        var data = CrowbarProjectSerializer.Deserialize(CrowbarProjectSerializer.Serialize(source));

        Assert.Equal(source.Format, data.Format);
        Assert.Equal(source.Id, data.Id);
        Assert.Equal(source.Name, data.Name);
        Assert.Equal(source.Version, data.Version);
        Assert.Equal(source.Author, data.Author);
        Assert.Equal(source.Description, data.Description);
    }

    [Fact]
    public void Serialize_WritesVersionedPrettyJson()
    {
        var json = CrowbarProjectSerializer.Serialize(BuildProjectData());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(CrowbarProjectFile.CurrentFormat, root.GetProperty("format").GetInt32());
        Assert.Equal("8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081", root.GetProperty("id").GetString());
        Assert.Equal("MyGame", root.GetProperty("name").GetString());
        Assert.Equal("0.1", root.GetProperty("version").GetString());
        Assert.Equal("Jane Doe", root.GetProperty("author").GetString());
        Assert.Equal("Un jeu de démonstration Crowbar.", root.GetProperty("description").GetString());
        // Pretty-printed (diff-friendly in VCS).
        Assert.Contains('\n', json);
    }

    [Fact]
    public void Deserialize_NewerFormat_Throws()
    {
        const string json = """
            { "format": 999, "id": "11111111-1111-1111-1111-111111111111", "name": "Future" }
            """;

        Assert.Throws<InvalidDataException>(() => CrowbarProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_OlderFormat_LoadsWithWarning()
    {
        const string json = """
            { "format": 0, "id": "11111111-1111-1111-1111-111111111111", "name": "Legacy" }
            """;

        var warnings = new List<string>();
        var data = CrowbarProjectSerializer.Deserialize(json, warnings.Add);

        Assert.Equal("Legacy", data.Name);
        Assert.Contains(warnings, w => w.Contains("older", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaveAndLoad_ThroughTheProjectFilesystem_PreservesTheProject()
    {
        var path = "CrowbarProjectFileTests_" + Guid.NewGuid().ToString("N") + ".crproj";
        try
        {
            CrowbarProjectFile.Save(BuildProjectData(), path);

            var file = CrowbarProjectFile.Load(path);
            Assert.Equal(new Guid("8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081"), file.Id);
            Assert.Equal("MyGame", file.Name);
            Assert.Equal("0.1", file.Version);
            Assert.Equal("Jane Doe", file.Author);
            Assert.Equal("Un jeu de démonstration Crowbar.", file.Description);
            Assert.True(file.IsValid);

            // The save is atomic: no leftover temp file.
            Assert.False(FileSystem.Project.FileExists(path + ".tmp"));
        }
        finally
        {
            if (FileSystem.Project.FileExists(path)) FileSystem.Project.DeleteFile(path);
            if (FileSystem.Project.FileExists(path + ".tmp")) FileSystem.Project.DeleteFile(path + ".tmp");
        }
    }

    [Fact]
    public void SaveToDisk_And_LoadFromDisk_RoundTrip_WithoutAnyConfiguredFilesystem()
    {
        // The disk-first path is the double-click startup: the .crproj must be
        // readable before any filesystem is configured, so it only touches the OS.
        var directory = Path.Combine(Path.GetTempPath(), "crowbar_project_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MyGame.crproj");
        try
        {
            CrowbarProjectFile.SaveToDisk(BuildProjectData(), path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var file = CrowbarProjectFile.LoadFromDisk(path);
            Assert.Equal("MyGame", file.Name);
            Assert.Equal("0.1", file.Version);
            Assert.Equal("Jane Doe", file.Author);
            Assert.Equal(new Guid("8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081"), file.Id);
            Assert.True(file.IsValid);
        }
        finally
        {
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDisk_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            CrowbarProjectFile.LoadFromDisk(Path.Combine(Path.GetTempPath(), "does_not_exist.crproj")));
    }
}