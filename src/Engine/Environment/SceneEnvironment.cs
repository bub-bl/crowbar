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
    private float _turbidity = 1f;
    private float _groundAlbedo = 0.3f;
    private float _sunAngularRadius = 1.5f; // degrees
    private float _sunIntensity = 1f;
    private Vector3 _sunDirection = Vector3.Normalize(new Vector3(-0.35f, 0.8f, -0.2f));

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

    /// <summary>Haze multiplier for Mie scattering; 1 = clear air.</summary>
    public float Turbidity
    {
        get => _turbidity;
        set => Set(ref _turbidity, Math.Clamp(value, 0.1f, 10f));
    }

    /// <summary>Diffuse reflectance of the ground (0..1), feeding the sky's ground bounce.</summary>
    public float GroundAlbedo
    {
        get => _groundAlbedo;
        set => Set(ref _groundAlbedo, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Angular radius of the sun disc, in degrees.</summary>
    public float SunAngularRadius
    {
        get => _sunAngularRadius;
        set => Set(ref _sunAngularRadius, Math.Clamp(value, 0.1f, 10f));
    }

    /// <summary>Multiplier on the sun's radiance.</summary>
    public float SunIntensity
    {
        get => _sunIntensity;
        set => Set(ref _sunIntensity, Math.Max(0f, value));
    }

    /// <summary>
    /// World-space sun direction driving the procedural sky. Set by the
    /// renderer each frame from the scene's first directional light (or a
    /// fallback); it is derived scene state, not an edited property, so it is
    /// never persisted and never marks the level dirty.
    /// </summary>
    internal Vector3 SunDirection
    {
        get => _sunDirection;
        set => _sunDirection = value;
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
