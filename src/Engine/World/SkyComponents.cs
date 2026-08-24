namespace Crowbar.Engine;

/// <summary>Provides an equirectangular HDR or EXR image as the scene sky.</summary>
[ComponentIcon("sun")]
public sealed class CubemapComponent : EnvironmentComponent
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
                SetSky(string.IsNullOrWhiteSpace(_sourcePath) ? null : new CubemapSky(_sourcePath));
            }
        }
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();
        SetSky(string.IsNullOrWhiteSpace(_sourcePath) ? null : new CubemapSky(_sourcePath));
    }
}

/// <summary>Provides the procedural atmospheric sky for the scene.</summary>
[ComponentIcon("sun")]
public sealed class ProceduralSkyComponent : EnvironmentComponent
{
    protected override void OnInitialize()
    {
        base.OnInitialize();
        SetSky(new ProceduralAtmosphere());
    }
}
