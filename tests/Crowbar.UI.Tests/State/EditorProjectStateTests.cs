namespace Crowbar.UI.Tests;

/// <summary>
/// Locks in the project-state bridge the top bar reads and the host consumes:
/// published name/path drive the panels, and the open-project request is
/// queued by the UI and consumed exactly once by the host per frame.
/// </summary>
public class EditorProjectStateTests
{
    [Fact]
    public void Publish_ExposesNamePathAndVersion()
    {
        EditorProjectState.Publish("MyGame", @"C:\Projects\MyGame\MyGame.crproj");

        Assert.Equal("MyGame", EditorProjectState.Name);
        Assert.Equal(@"C:\Projects\MyGame\MyGame.crproj", EditorProjectState.Path);
        Assert.True(EditorProjectState.HasProject);
        Assert.Equal(1, EditorProjectState.Version);
    }

    [Fact]
    public void Publish_IdenticalState_DoesNotBumpTheVersion()
    {
        EditorProjectState.Publish("MyGame", @"C:\Projects\MyGame\MyGame.crproj");
        var version = EditorProjectState.Version;

        EditorProjectState.Publish("MyGame", @"C:\Projects\MyGame\MyGame.crproj");

        Assert.Equal(version, EditorProjectState.Version);
    }

    [Fact]
    public void Publish_EmptyState_MeansNoProject()
    {
        EditorProjectState.Publish("", "");

        Assert.False(EditorProjectState.HasProject);
    }

    [Fact]
    public void RequestOpen_IsConsumedExactlyOnce()
    {
        EditorProjectState.RequestOpen();

        Assert.True(EditorProjectState.ConsumeOpenRequest());
        Assert.False(EditorProjectState.ConsumeOpenRequest());
    }
}