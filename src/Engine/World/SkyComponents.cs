namespace Crowbar.Engine;

/// <summary>Provides an equirectangular HDR or EXR image as the scene sky.</summary>
[ComponentIcon("sun")]
public sealed class CubemapComponent : Component
{
    private string _sourcePath = string.Empty;

    [Property]
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(_sourcePath, value, StringComparison.Ordinal))
            {
                _sourcePath = value;
                SyncEnvironment();
            }
        }
    }

    protected override void OnInitialize() => SyncEnvironment();

    private void SyncEnvironment()
    {
        if (Entity?.GetComponent<EnvironmentComponent>() is { } environment)
            environment.SetSky(string.IsNullOrWhiteSpace(_sourcePath) ? null : new CubemapSky(_sourcePath));
    }
}

/// <summary>Provides the procedural atmospheric sky for the scene.</summary>
[ComponentIcon("sun")]
public sealed class ProceduralSkyComponent : Component
{
    protected override void OnInitialize() => SyncEnvironment();

    private void SyncEnvironment()
    {
        Entity?.GetComponent<EnvironmentComponent>()?.SetSky(new ProceduralAtmosphere());
    }
}
