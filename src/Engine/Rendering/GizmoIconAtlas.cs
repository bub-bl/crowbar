using System.Numerics;
using Crowbar.Engine.Rendering2D;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// The icon displayed for a component's viewport gizmo. The <see cref="Name"/>
/// matches an SVG file in <c>Assets/Gizmos/&lt;name&gt;.svg</c>; components
/// declare theirs with <see cref="GizmoIconAttribute"/>.
/// </summary>
public readonly record struct GizmoIcon(string Name)
{
    public static GizmoIcon DirectionalLight { get; } = new("directional-light");
    public static GizmoIcon PointLight { get; } = new("point-light");
    public static GizmoIcon Mesh { get; } = new("mesh");
    public static GizmoIcon Camera { get; } = new("camera");
}

/// <summary>
/// GPU atlas containing the editor-only SVG gizmo icons. SVG remains the source
/// asset; this class discovers every <c>*.svg</c> in the <c>Assets/Gizmos</c>
/// directory, rasterizes them once (with the engine's own SVG parser and CPU
/// rasterizer — no Skia) when the renderer is created and exposes one
/// texture/sampler pair plus normalized UV rectangles for the sprite shader.
/// </summary>
public sealed class GizmoIconAtlas : IDisposable
{
    public const int IconSize = 32;

    private readonly ITexture _texture;
    private readonly ISampler _sampler;
    private readonly Dictionary<string, Vector4> _uvByName;
    private bool _disposed;

    private GizmoIconAtlas(
        ITexture texture,
        ISampler sampler,
        Dictionary<string, Vector4> uvByName)
    {
        _texture = texture;
        _sampler = sampler;
        _uvByName = uvByName;
    }

    public ITexture Texture => _texture;

    public ISampler Sampler => _sampler;

    /// <summary>Whether the atlas contains an icon with the given name.</summary>
    public bool Contains(GizmoIcon icon) => _uvByName.ContainsKey(icon.Name);

    /// <summary>The normalized atlas rectangle (u0, v0, u1, v1) of an icon.</summary>
    public Vector4 GetUv(GizmoIcon icon)
    {
        if (_uvByName.TryGetValue(icon.Name, out var uv))
            return uv;

        throw new KeyNotFoundException(
            $"Gizmo icon '{icon.Name}' is not in the atlas. Add Assets/Gizmos/{icon.Name}.svg to the editor assets.");
    }

    /// <summary>
    /// Loads every SVG icon from <c>Assets/Gizmos</c>. The icon name is the file
    /// name without its extension, so decorating a component with
    /// <c>[GizmoIcon("name")]</c> needs nothing else.
    /// </summary>
    public static GizmoIconAtlas Load(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var fs = FileSystem.Content;
        var assetsDirectory = PathUtil.Combine("Assets", "Gizmos");
        if (!fs.DirectoryExists(assetsDirectory))
            throw new DirectoryNotFoundException(
                $"The gizmo icon directory '{assetsDirectory}' was not found.");

        var files = fs.EnumerateFiles(assetsDirectory, "*.svg")
            .OrderBy(path => path.FullName, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"No SVG gizmo icons were found in '{assetsDirectory}'.");

        var width = IconSize * files.Count;
        var pixels = new byte[width * IconSize * 4];
        var uvByName = new Dictionary<string, Vector4>(StringComparer.Ordinal);

        for (var index = 0; index < files.Count; index++)
        {
            var fileName = files[index].GetNameWithoutExtension() ?? string.Empty;
            var iconPixels = RasterizeSvg(files[index], IconSize);
            for (var row = 0; row < IconSize; row++)
            {
                var sourceOffset = row * IconSize * 4;
                var destinationOffset = (row * width + index * IconSize) * 4;
                Array.Copy(iconPixels, sourceOffset, pixels, destinationOffset, IconSize * 4);
            }

            uvByName[fileName] = new Vector4(
                index / (float)files.Count,
                0f,
                (index + 1) / (float)files.Count,
                1f);
        }

        var texture = device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = IconSize,
            Format = TextureFormat.Rgba8Unorm,
            Sampled = true,
            CopyDestination = true
        });
        unsafe
        {
            fixed (byte* data = pixels)
                texture.Write((nint)data, width * 4, 0, 0, width, IconSize);
        }

        var sampler = device.CreateSampler(new SamplerDescription
        {
            Filter = SamplerFilter.Linear,
            AddressMode = SamplerAddressMode.ClampToEdge,
            MipmapFilter = SamplerFilter.Nearest
        });

        return new GizmoIconAtlas(texture, sampler, uvByName);
    }

    private static byte[] RasterizeSvg(FilePath path, int size)
    {
        // The supplied icons use currentColor: the engine's parser treats it as
        // a caller tint, so rasterize white and let the sprite shader multiply
        // the sampled RGB by the light/entity tint.
        var shape = SvgDocumentParser.Parse(FileSystem.Content.ReadAllText(path));
        if (shape.ViewBox.Width <= 0f || shape.ViewBox.Height <= 0f)
            throw new InvalidDataException($"SVG gizmo icon '{path}' has no drawable bounds.");

        return SvgRasterizer.Rasterize(shape, size, ColorF.White);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sampler.Dispose();
        _texture.Dispose();
    }
}
