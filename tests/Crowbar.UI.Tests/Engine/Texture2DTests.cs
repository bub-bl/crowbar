using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Crowbar.Engine.Tests;

public class Texture2DTests
{
    [Fact]
    public void Load_DecodesPngIntoRgba8Pixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crowbar-tex-{Guid.NewGuid():N}.png");
        using (var image = new Image<Rgba32>(2, 1))
        {
            image[0, 0] = new Rgba32(255, 0, 0, 255);
            image[1, 0] = new Rgba32(0, 255, 0, 128);
            image.Save(path);
        }

        try
        {
            var texture = Texture2D.Load(path);

            Assert.Equal("crowbar-tex", texture.Name.Substring(0, "crowbar-tex".Length));
            Assert.Equal(2, texture.Width);
            Assert.Equal(1, texture.Height);
            Assert.Equal(2 * 1 * 4, texture.Pixels.Length);

            // Row 0: red opaque, then green half-transparent.
            Assert.Equal([255, 0, 0, 255], texture.Pixels[..4]);
            Assert.Equal([0, 255, 0, 128], texture.Pixels[4..8]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(
            () => Texture2D.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png")));
    }

    [Fact]
    public void GetMipPixels_AveragesEach2x2Block()
    {
        // Per-pixel values chosen so every channel averages exactly, avoiding
        // rounding ambiguity in the box filter.
        var texture = Texture2D.Create("mip", 2, 2,
        [
            0, 0, 0, 0,       // (0,0)
            4, 8, 12, 16,     // (1,0)
            8, 16, 24, 32,    // (0,1)
            12, 24, 36, 48    // (1,1)
        ]);

        Assert.Equal(2, texture.MipLevelCount); // 2x2 -> 1x1
        Assert.Equal(2, texture.GetMipWidth(0));
        Assert.Equal(2, texture.GetMipHeight(0));
        Assert.Equal(1, texture.GetMipWidth(1));
        Assert.Equal(1, texture.GetMipHeight(1));

        var mip = texture.GetMipPixels(1);
        Assert.Equal([6, 12, 18, 24], mip);
        Assert.Same(mip, texture.GetMipPixels(1)); // cached
    }

    [Fact]
    public void MipLevels_HalveEachDimensionDownToOne()
    {
        // Non-power-of-two: 5x3 -> 2x1 -> 1x1.
        var texture = Texture2D.Create("mip", 5, 3, new byte[5 * 3 * 4]);

        Assert.Equal(3, texture.MipLevelCount);
        Assert.Equal(5, texture.GetMipWidth(0));
        Assert.Equal(2, texture.GetMipWidth(1));
        Assert.Equal(1, texture.GetMipWidth(2));
        Assert.Equal(3, texture.GetMipHeight(0));
        Assert.Equal(1, texture.GetMipHeight(1));
        Assert.Equal(1, texture.GetMipHeight(2));

        Assert.Equal(5 * 3 * 4, texture.GetMipPixels(0).Length);
        Assert.Equal(2 * 1 * 4, texture.GetMipPixels(1).Length);
        Assert.Equal(1 * 1 * 4, texture.GetMipPixels(2).Length);
    }

    [Fact]
    public void Load_DefersDecodeUntilPixelsAreFirstRead()
    {
        // Decoding is lazy: importing a model must record its texture set
        // without decoding every image up front. Loading only validates the
        // file; the pixels are read on first access.
        var path = Path.Combine(Path.GetTempPath(), $"crowbar-lazy-{Guid.NewGuid():N}.png");
        using (var image = new Image<Rgba32>(2, 1))
        {
            image[0, 0] = new Rgba32(255, 0, 0, 255);
            image[1, 0] = new Rgba32(0, 255, 0, 128);
            image.Save(path);
        }

        try
        {
            var texture = Texture2D.Load(path); // succeeds without decoding

            // Remove the backing file: decoding hasn't happened yet, so the
            // first read now fails, proving the decode was deferred.
            File.Delete(path);
            Assert.ThrowsAny<IOException>(() => texture.Width);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
