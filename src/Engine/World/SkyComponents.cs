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
    /// <summary>Haze multiplier for Mie scattering; 1 = clear air.</summary>
    [Property]
    public float Turbidity
    {
        get => Environment.Turbidity;
        set => Environment.Turbidity = value;
    }

    /// <summary>Diffuse reflectance of the ground (0..1), feeding the sky's ground bounce.</summary>
    [Property]
    public float GroundAlbedo
    {
        get => Environment.GroundAlbedo;
        set => Environment.GroundAlbedo = value;
    }

    /// <summary>Angular radius of the sun disc, in degrees.</summary>
    [Property]
    public float SunAngularRadius
    {
        get => Environment.SunAngularRadius;
        set => Environment.SunAngularRadius = value;
    }

    /// <summary>Multiplier on the sun's radiance.</summary>
    [Property]
    public float SunIntensity
    {
        get => Environment.SunIntensity;
        set => Environment.SunIntensity = value;
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();
        SetSky(new ProceduralAtmosphere());
    }
}
