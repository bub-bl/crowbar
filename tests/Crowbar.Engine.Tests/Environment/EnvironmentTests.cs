using System.Numerics;
using System.Text;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Tests;

public sealed class EnvironmentTests
{
    [Fact]
    public void LevelRoundTrip_PreservesEnvironmentSettings()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("Environment");
        source.Environment.Sky = new CubemapSky("Content/Environments/studio.exr");
        source.Environment.Rotation = 1.25f;
        source.Environment.Intensity = 2.5f;
        source.Environment.Exposure = -0.75f;
        source.Environment.Tint = new Vector4(0.8f, 0.9f, 1f, 1f);

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var sky = Assert.IsType<CubemapSky>(loaded.Environment.Sky);
        Assert.Equal("Content/Environments/studio.exr", sky.SourcePath);
        Assert.Equal(1.25f, loaded.Environment.Rotation);
        Assert.Equal(2.5f, loaded.Environment.Intensity);
        Assert.Equal(-0.75f, loaded.Environment.Exposure);
        Assert.Equal(new Vector4(0.8f, 0.9f, 1f, 1f), loaded.Environment.Tint);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void LegacyEnvironmentComponent_MigratesToDedicatedSkyComponent()
    {
        const string json = """
            {
              "format": 2,
              "id": "11111111-1111-1111-1111-111111111111",
              "metadata": { "name": "Legacy" },
              "environment": null,
              "entities": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "name": "Environment",
                  "components": [
                    {
                      "type": "EnvironmentComponent",
                      "properties": {
                        "Provider": "ProceduralAtmosphere",
                        "Intensity": 1.5
                      }
                    }
                  ]
                }
              ],
              "attachments": []
            }
            """;

        using var world = new World();
        var level = LevelSerializer.CreateLevel(world, LevelSerializer.Deserialize(json));
        var entity = Assert.Single(level.Entities);

        Assert.IsType<ProceduralSkyComponent>(Assert.Single(entity.Components));
        Assert.Equal(1.5f, entity.GetComponent<ProceduralSkyComponent>()?.Intensity);
        Assert.IsType<ProceduralAtmosphere>(entity.GetComponent<EnvironmentComponent>()?.Environment.Sky);
    }

    [Fact]
    public void InspectorEdit_ActivatesProceduralAtmosphere()
    {
        using var world = new World();
        var level = world.CreateLevel("Procedural");
        var entity = world.SpawnEntity("Environment", level);
        var sky = entity.AddComponent<ProceduralSkyComponent>();

        Assert.IsType<ProceduralAtmosphere>(sky.Environment.Sky);
    }

    [Fact]
    public void ProceduralEnvironment_RoundTripPreservesProvider()
    {
        using var sourceWorld = new World();
        var source = sourceWorld.CreateLevel("Procedural");
        var environmentEntity = sourceWorld.SpawnEntity("Environment", source);
        var component = environmentEntity.AddComponent<ProceduralSkyComponent>();
        component.Intensity = 1.5f;

        using var loadedWorld = new World();
        var loaded = LevelSerializer.CreateLevel(
            loadedWorld,
            LevelSerializer.Deserialize(LevelSerializer.Serialize(source)));

        var loadedEnvironment = Assert.Single(loaded.Entities, entity => entity.Name == "Environment");
        var loadedSky = Assert.IsType<ProceduralSkyComponent>(loadedEnvironment.GetComponent<ProceduralSkyComponent>());
        Assert.IsType<ProceduralAtmosphere>(loadedSky.Environment.Sky);
        Assert.Equal(1.5f, loadedSky.Intensity);
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void EnvironmentEdit_IsDirtyAndUndoable()
    {
        using var world = new World();
        var level = world.CreateLevel("Environment");
        _ = level.History;

        using (level.History.Step("Edit the environment"))
            level.Environment.Exposure = 2f;

        Assert.True(level.IsDirty);
        level.History.Undo();
        Assert.Equal(0f, level.Environment.Exposure);
        level.History.Redo();
        Assert.Equal(2f, level.Environment.Exposure);
    }

    [Fact]
    public void UnsupportedProvider_FallsBackToNeutralEnvironment()
    {
        const string json = """
            { "format": 1, "id": "11111111-1111-1111-1111-111111111111",
              "environment": { "provider": "FutureSky", "rotation": 4, "intensity": 3, "exposure": 1,
                "tint": [1, 1, 1, 1] }, "entities": [] }
            """;
        var warnings = new List<string>();
        using var world = new World();
        var level = LevelSerializer.CreateLevel(
            world,
            LevelSerializer.Deserialize(json, warnings.Add),
            warnings.Add);

        Assert.Null(level.Environment.Sky);
        Assert.Contains(warnings, warning => warning.Contains("Unsupported sky provider", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentMath_IsDeterministic()
    {
        Assert.Equal(0f, EnvironmentMath.RadicalInverse(0));
        Assert.Equal(0.5f, EnvironmentMath.RadicalInverse(1));
        Assert.Equal(new Vector2(0.25f, 0.5f), EnvironmentMath.Hammersley(1, 4));
        Assert.Equal(3f, EnvironmentMath.RoughnessToMip(0.5f, 7));
        var rotated = EnvironmentMath.RotateY(Vector3.UnitX, MathF.PI / 2f);
        Assert.Equal(0f, rotated.X, 5);
        Assert.Equal(0f, rotated.Y, 5);
        Assert.Equal(1f, rotated.Z, 5);
    }

    [Fact]
    public void RadianceDecoder_RejectsMalformedHeaders()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("not hdr\n"));
        Assert.Throws<InvalidDataException>(() => RadianceHdrDecoder.Decode(stream));
    }

    [Fact]
    public void RadianceDecoder_DecodesLinearRgbePixels()
    {
        byte[] header = Encoding.ASCII.GetBytes(
            "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
        using var stream = new MemoryStream([.. header, 128, 64, 32, 129]);

        var image = RadianceHdrDecoder.Decode(stream);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal([1f, 0.5f, 0.25f, 1f], image.Pixels);
    }

    [Fact]
    public void Texture2D_HdrDataRetainsLinearFloatFormat()
    {
        var texture = Texture2D.CreateHdr("test", 1, 1, [2f, 1f, 0.5f, 1f]);

        Assert.True(texture.IsHdr);
        Assert.Equal(Texture2D.SourcePixelFormat.Rgba16Float, texture.PixelFormat);
        Assert.Equal(2f, texture.HdrPixels[0]);
        Assert.Equal(8, texture.GetRgba16FloatBytes().Length);
    }

    [Fact]
    public void Texture2D_ExrDecodeRetainsLinearHdrPixels()
    {
        var path = $"Content/environment-{Guid.NewGuid():N}.exr";
        try
        {
            FileSystem.Project.WriteAllBytes(path, CreateFloatExr(2f, 1f, 0.5f, 1f));

            var texture = Texture2D.Load(path);

            Assert.True(texture.IsHdr);
            Assert.Equal(2f, texture.HdrPixels[0], 3);
            Assert.Equal(1f, texture.HdrPixels[1], 3);
            Assert.Equal(0.5f, texture.HdrPixels[2], 3);
        }
        finally
        {
            Texture2D.Invalidate(path);
            if (FileSystem.Project.FileExists(path))
                FileSystem.Project.DeleteFile(path);
        }
    }

    private static byte[] CreateFloatExr(float red, float green, float blue, float alpha)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(20000630);
        writer.Write(2);
        WriteExrAttribute(writer, "channels", "chlist", channelWriter =>
        {
            foreach (var name in new[] { "B", "G", "R", "A" })
            {
                WriteNullTerminated(channelWriter, name);
                channelWriter.Write(2); // FLOAT
                channelWriter.Write((byte)0);
                channelWriter.Write(new byte[3]);
                channelWriter.Write(1);
                channelWriter.Write(1);
            }
            channelWriter.Write((byte)0);
        });
        WriteExrAttribute(writer, "compression", "compression", value => value.Write((byte)0));
        WriteExrAttribute(writer, "dataWindow", "box2i", value =>
        {
            value.Write(0);
            value.Write(0);
            value.Write(0);
            value.Write(0);
        });
        WriteExrAttribute(writer, "displayWindow", "box2i", value =>
        {
            value.Write(0);
            value.Write(0);
            value.Write(0);
            value.Write(0);
        });
        WriteExrAttribute(writer, "lineOrder", "lineOrder", value => value.Write((byte)0));
        WriteExrAttribute(writer, "pixelAspectRatio", "float", value => value.Write(1f));
        WriteExrAttribute(writer, "screenWindowCenter", "v2f", value =>
        {
            value.Write(0f);
            value.Write(0f);
        });
        WriteExrAttribute(writer, "screenWindowWidth", "float", value => value.Write(1f));
        writer.Write((byte)0);

        writer.Write(stream.Position + sizeof(long));
        writer.Write(0);
        writer.Write(4 * sizeof(float));
        writer.Write(blue);
        writer.Write(green);
        writer.Write(red);
        writer.Write(alpha);
        return stream.ToArray();
    }

    private static void WriteExrAttribute(
        BinaryWriter writer,
        string name,
        string type,
        Action<BinaryWriter> writeValue)
    {
        WriteNullTerminated(writer, name);
        WriteNullTerminated(writer, type);
        using var valueStream = new MemoryStream();
        using (var valueWriter = new BinaryWriter(valueStream, Encoding.ASCII, leaveOpen: true))
            writeValue(valueWriter);
        writer.Write(checked((int)valueStream.Length));
        writer.Write(valueStream.ToArray());
    }

    private static void WriteNullTerminated(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }

    [Fact]
    public void ExrEnvironmentAsset_IsAvailableInProjectContent()
    {
        Assert.True(FileSystem.Content.FileExists("Content/Environments/studio_b.exr"));

        var texture = Texture2D.Load("Content/Environments/studio_b.exr");
        try
        {
            Assert.True(texture.IsHdr);
            Assert.Equal(Texture2D.SourcePixelFormat.Rgba16Float, texture.PixelFormat);
            Assert.True(texture.Width > 0);
            Assert.True(texture.Height > 0);
            var pixels = texture.HdrPixels;
            Assert.Equal(texture.Width * texture.Height * 4, pixels.Length);
            Assert.Contains(pixels, pixel => float.IsFinite(pixel) && pixel > 0f);
        }
        finally
        {
            Texture2D.Invalidate("Content/Environments/studio_b.exr");
        }
    }

    [Fact]
    public void CubeTextureDescriptionAndFaceMipViews_AreValidated()
    {
        var cube = new TextureDescription
        {
            Width = 128,
            Height = 128,
            Dimension = TextureDimension.Cube,
            ArrayLayerCount = 6,
            MipLevelCount = 8,
            Format = TextureFormat.Rgba16Float,
            Sampled = true,
            Storage = true
        };

        WebGpuTexture.ValidateDescription(cube);
        WebGpuTexture.ValidateViewDescription(
            cube.Dimension, cube.MipLevelCount, cube.ArrayLayerCount,
            new TextureViewDescription
            {
                Dimension = TextureDimension.Dimension2D,
                BaseMipLevel = 4,
                MipLevelCount = 1,
                BaseArrayLayer = 5,
                ArrayLayerCount = 1
            });
        WebGpuTexture.ValidateViewDescription(
            cube.Dimension, cube.MipLevelCount, cube.ArrayLayerCount,
            new TextureViewDescription
            {
                Dimension = TextureDimension.Cube,
                BaseMipLevel = 2,
                MipLevelCount = 1,
                ArrayLayerCount = 6
            });

        Assert.Throws<ArgumentException>(() => WebGpuTexture.ValidateDescription(new TextureDescription
        {
            Width = 16,
            Height = 16,
            Dimension = TextureDimension.Cube,
            ArrayLayerCount = 5
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebGpuTexture.ValidateViewDescription(
            cube.Dimension, cube.MipLevelCount, cube.ArrayLayerCount,
            new TextureViewDescription { BaseMipLevel = 8 }));
    }

    [Fact]
    public void SkyShader_ExposesProceduralProviderUniform()
    {
        var shader = Shader.Load("Shaders/Environment/Sky.wgsl");
        var environment = Assert.Single(shader.Structs, structure => structure.Name == "EnvironmentUniforms");

        Assert.Contains(environment.Fields, field => field.Name == "provider");
    }

    [Fact]
    public void SkyPipeline_UsesFullscreenTriangleAndReadOnlyFarDepth()
    {
        var description = Renderer.CreateSkyPipelineDescription(
            Shader.Load("Shaders/Environment/Sky.wgsl"),
            TextureFormat.Bgra8UnormSrgb);

        Assert.False(description.DepthWriteEnabled);
        Assert.Equal(CompareFunction.LessEqual, description.DepthCompare);
        Assert.Equal(0ul, description.VertexLayout.Stride);
        Assert.Empty(description.VertexLayout.Attributes);
    }
}
