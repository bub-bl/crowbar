using System.Runtime.InteropServices;
using Crowbar.UI;
using SkiaSharp;

namespace Crowbar.UI.Tests.Rendering;

/// <summary>DEBUG-ONLY: renders the editor page to a PNG for visual comparison.</summary>
public class EditorSnapshotDump
{
    [Fact]
    public void DumpEditorSnapshot()
    {
        var ui = EditorPageCompositionTests.CreateEditorUi();
        ui.Renderer.Resize(1280, 720);
        ui.Screen.SetViewport(1280, 720);
        ui.Render();

        var n = ui.Renderer.PixelBuffer;
        var rowBytes = ui.Renderer.RowBytes;
        var bytes = new byte[rowBytes * 720];
        Marshal.Copy(n, bytes, 0, bytes.Length);

        var info = new SKImageInfo(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), rowBytes);
            using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(@"C:\tmp\editor_dump.png");
            data.SaveTo(stream);
        }
        finally
        {
            handle.Free();
        }
    }
}
