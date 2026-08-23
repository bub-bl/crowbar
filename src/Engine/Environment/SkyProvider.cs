namespace Crowbar.Engine;

public enum SkyProviderKind
{
    None,
    Cubemap,
    ProceduralAtmosphere
}

public abstract class SkyProvider
{
    public abstract SkyProviderKind Kind { get; }
}

public sealed class CubemapSky : SkyProvider
{
    public CubemapSky(string sourcePath = "") => SourcePath = sourcePath ?? string.Empty;

    public override SkyProviderKind Kind => SkyProviderKind.Cubemap;

    public string SourcePath { get; }
}

public sealed class ProceduralAtmosphere : SkyProvider
{
    public override SkyProviderKind Kind => SkyProviderKind.ProceduralAtmosphere;
}
