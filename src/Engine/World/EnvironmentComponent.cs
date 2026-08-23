using System.Numerics;

namespace Crowbar.Engine;

/// <summary>Provides the scene sky and image-based lighting for an entity.</summary>
[ComponentIcon("sun")]
public sealed class EnvironmentComponent : Component
{
    private readonly SceneEnvironment _environment = new();
    private SkyProviderKind _provider;
    private string _sourcePath = string.Empty;
    private float _rotation;
    private float _intensity = 1f;
    private float _exposure;
    private Vector4 _tint = Vector4.One;

    public SceneEnvironment Environment => _environment;

    [Property]
    public SkyProviderKind Provider
    {
        get => _provider;
        set { if (_provider != value) { _provider = value; Sync(); } }
    }

    [Property]
    public string SourcePath
    {
        get => _sourcePath;
        set { value ??= string.Empty; if (!string.Equals(_sourcePath, value, StringComparison.Ordinal)) { _sourcePath = value; Sync(); } }
    }

    [Property]
    public float Rotation { get => _rotation; set { if (!_rotation.Equals(value)) { _rotation = value; Sync(); } } }

    [Property]
    public float Intensity { get => _intensity; set { var v = Math.Max(0f, value); if (!_intensity.Equals(v)) { _intensity = v; Sync(); } } }

    [Property]
    public float Exposure { get => _exposure; set { if (!_exposure.Equals(value)) { _exposure = value; Sync(); } } }

    [Property]
    public Vector4 Tint { get => _tint; set { if (_tint != value) { _tint = value; Sync(); } } }

    protected override void OnInitialize()
    {
        _environment.AttachOwner(Entity?.Level);
        Sync();
    }

    protected override void OnDestroy() => _environment.Dispose();

    internal void RestoreFrom(SceneEnvironment source)
    {
        _provider = source.Sky?.Kind ?? SkyProviderKind.None;
        _sourcePath = (source.Sky as CubemapSky)?.SourcePath ?? string.Empty;
        _rotation = source.Rotation;
        _intensity = source.Intensity;
        _exposure = source.Exposure;
        _tint = source.Tint;
        Sync();
    }

    private void Sync()
    {
        SkyProvider? sky = _provider switch
        {
            SkyProviderKind.Cubemap when !string.IsNullOrWhiteSpace(_sourcePath) => new CubemapSky(_sourcePath),
            SkyProviderKind.ProceduralAtmosphere => new ProceduralAtmosphere(),
            _ => null
        };
        _environment.Restore(sky, _rotation, _intensity, _exposure, _tint);
    }
}
