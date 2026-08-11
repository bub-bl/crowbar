using System.Numerics;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Skia;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// GPU atlas containing the editor-only SVG gizmo icons. SVG remains the source
/// asset; this class rasterizes it once when the renderer is created and exposes
/// one texture/sampler pair plus normalized UV rectangles for the sprite shader.
/// </summary>
public sealed class GizmoIconAtlas : IDisposable
{
    public const int IconSize = 32;

    private static readonly (GizmoIcon Icon, string FileName)[] Definitions =
    [
        (GizmoIcon.DirectionalLight, "directional-light.svg"),
        (GizmoIcon.PointLight, "point-light.svg"),
        (GizmoIcon.Mesh, "mesh.svg"),
        (GizmoIcon.Camera, "camera.svg")
    ];

    private readonly ITexture _texture;
    private readonly ISampler _sampler;
    private bool _disposed;

    private GizmoIconAtlas(ITexture texture, ISampler sampler)
    {
        _texture = texture;
        _sampler = sampler;
    }

    public ITexture Texture => _texture;

    public ISampler Sampler => _sampler;

    public static GizmoIconAtlas Load(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var width = IconSize * Definitions.Length;
        var pixels = new byte[width * IconSize * 4];
        var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Gizmos");

        for (var index = 0; index < Definitions.Length; index++)
        {
            var path = Path.Combine(assetsDirectory, Definitions[index].FileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Gizmo icon '{Definitions[index].FileName}' was not found.", path);

            var iconPixels = RasterizeSvg(path, IconSize);
            for (var row = 0; row < IconSize; row++)
            {
                var sourceOffset = row * IconSize * 4;
                var destinationOffset = (row * width + index * IconSize) * 4;
                Array.Copy(iconPixels, sourceOffset, pixels, destinationOffset, IconSize * 4);
            }
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

        return new GizmoIconAtlas(texture, sampler);
    }

    public Vector4 GetUv(GizmoIcon icon)
    {
        var index = Array.FindIndex(Definitions, definition => definition.Icon == icon);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(icon), icon, "The gizmo icon is not part of the atlas.");

        return new Vector4(
            index / (float)Definitions.Length,
            0f,
            (index + 1) / (float)Definitions.Length,
            1f);
    }

    private static byte[] RasterizeSvg(string path, int size)
    {
        // The supplied icons use currentColor. Rasterize them white, then let
        // the GPU multiply the sampled RGB by the light/entity tint.
        var source = File.ReadAllText(path).Replace("currentColor", "#FFFFFF", StringComparison.Ordinal);
        using var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var svg = new SKSvg();
        if (svg.Load(sourceStream) is null || svg.Picture is null)
            throw new InvalidDataException($"Could not rasterize SVG gizmo icon '{path}'.");

        var bounds = svg.Picture.CullRect;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            throw new InvalidDataException($"SVG gizmo icon '{path}' has no drawable bounds.");

        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var scale = MathF.Min(size / bounds.Width, size / bounds.Height);
            canvas.Save();
            canvas.Translate(
                (size - bounds.Width * scale) * 0.5f - bounds.Left * scale,
                (size - bounds.Height * scale) * 0.5f - bounds.Top * scale);
            canvas.Scale(scale);
            canvas.DrawPicture(svg.Picture);
            canvas.Restore();
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException($"Could not encode SVG gizmo icon '{path}'.");
        using var pngStream = encoded.AsStream();
        using var raster = Image.Load<Rgba32>(pngStream);
        var pixels = new byte[size * size * 4];
        raster.CopyPixelDataTo(pixels);
        return pixels;
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

public enum GizmoIcon
{
    DirectionalLight,
    PointLight,
    Mesh,
    Camera
}
