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
}
