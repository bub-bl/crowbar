using System.Numerics;
using Crowbar.Engine.Rendering;

namespace Crowbar.Engine;

public enum EnvironmentPreprocessingState
{
    Empty,
    Pending,
    Decoding,
    Processing,
    Ready,
    Unavailable,
    Failed
}

public sealed class SceneEnvironment : IDisposable
{
    private Level? _owner;
    private SkyProvider? _sky;
    private float _rotation;
    private float _intensity = 1f;
    private float _exposure;
    private Vector4 _tint = Vector4.One;

    internal SceneEnvironment(Level? owner = null) => _owner = owner;

    internal void AttachOwner(Level? owner) => _owner = owner;

    public SkyProvider? Sky
    {
        get => _sky;
        set
        {
            if (ProvidersEqual(_sky, value))
                return;
            _sky = value;
            InvalidateRuntime();
            _owner?.MarkDirty();
        }
    }

    public float Rotation
    {
        get => _rotation;
        set => Set(ref _rotation, value);
    }

    public float Intensity
    {
        get => _intensity;
        set => Set(ref _intensity, Math.Max(0f, value));
    }

    public float Exposure
    {
        get => _exposure;
        set => Set(ref _exposure, value);
    }

    public Vector4 Tint
    {
        get => _tint;
        set => Set(ref _tint, value);
    }

    public EnvironmentPreprocessingState State { get; internal set; }
    public string? Diagnostic { get; internal set; }
    public ITexture? EnvironmentMap { get; internal set; }
    public ITexture? IrradianceMap { get; internal set; }
    public ITexture? PrefilteredSpecularMap { get; internal set; }
    public ITexture? BrdfLut { get; internal set; }

    public void SetCubemap(string? sourcePath) =>
        Sky = string.IsNullOrWhiteSpace(sourcePath) ? null : new CubemapSky(sourcePath);

    internal void Restore(
        SkyProvider? sky,
        float rotation,
        float intensity,
        float exposure,
        Vector4 tint)
    {
        _sky = sky;
        _rotation = rotation;
        _intensity = Math.Max(0f, intensity);
        _exposure = exposure;
        _tint = tint;
        InvalidateRuntime();
    }

    internal void InvalidateRuntime()
    {
        EnvironmentMap = null;
        IrradianceMap = null;
        PrefilteredSpecularMap = null;
        BrdfLut = null;
        Diagnostic = null;
        State = Sky is null ? EnvironmentPreprocessingState.Empty : EnvironmentPreprocessingState.Pending;
    }

    public void Dispose() => InvalidateRuntime();

    private void Set(ref float field, float value)
    {
        if (field.Equals(value))
            return;
        field = value;
        _owner?.MarkDirty();
    }

    private void Set(ref Vector4 field, Vector4 value)
    {
        if (field == value)
            return;
        field = value;
        _owner?.MarkDirty();
    }

    private static bool ProvidersEqual(SkyProvider? left, SkyProvider? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (CubemapSky a, CubemapSky b) => string.Equals(a.SourcePath, b.SourcePath, StringComparison.Ordinal),
            (ProceduralAtmosphere, ProceduralAtmosphere) => true,
            _ => false
        };
}
