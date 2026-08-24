using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

/// <summary>Owns the shared scene environment settings.</summary>
[ComponentIcon("sun")]
public sealed class EnvironmentComponent : Component
{
    private readonly SceneEnvironment _environment = new();
    private float _rotation;
    private float _intensity = 1f;
    private float _exposure;
    private Vector4 _tint = Vector4.One;

    public SceneEnvironment Environment => _environment;

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
        _environment.Restore(source.Sky, source.Rotation, source.Intensity, source.Exposure, source.Tint);
        _rotation = source.Rotation;
        _intensity = source.Intensity;
        _exposure = source.Exposure;
        _tint = source.Tint;
        Sync();
    }

    internal void SetSky(SkyProvider? sky) => _environment.Restore(
        sky,
        _rotation,
        _intensity,
        _exposure,
        _tint);

    private void Sync()
    {
        _environment.Restore(
            _environment.Sky,
            _rotation,
            _intensity,
            _exposure,
            _tint);
    }
}
