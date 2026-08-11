using Crowbar.Engine;

namespace Crowbar.UI.Tests.Rendering;

public class WebGpuTextureUploadTests
{
    [Fact]
    public void PartialUploadStartsAtDamageOriginInSourceBitmap()
    {
        var source = (nint)0x1000;
        var sourceRowBytes = 1280 * 4;

        var uploadSource = WebGpuTexture.GetUploadSource(
            source, sourceRowBytes, x: 320, y: 180, bytesPerPixel: 4);

        Assert.Equal(source + 180 * sourceRowBytes + 320 * 4, uploadSource);
    }

    [Fact]
    public void FullUploadKeepsSourcePointerUnchanged()
    {
        var source = (nint)0x1000;

        Assert.Equal(source, WebGpuTexture.GetUploadSource(source, 5120, 0, 0, 4));
    }
}
