using System.Numerics;

namespace Crowbar.Engine.Tests;

public class UniformPackerTests
{
    private static readonly ShaderStructField[] MaterialFields =
    [
        new("color", "vec4<f32>"),
        new("metallic", "f32"),
        new("roughness", "f32"),
        new("emissive", "f32")
    ];

    [Fact]
    public void PacksVec4ThenFloatsWith16ByteAlignment()
    {
        var bytes = UniformPacker.Pack(MaterialFields, new Dictionary<string, ShaderParameter>
        {
            ["color"] = new Vector4(0.2f, 0.6f, 1f, 1f),
            ["metallic"] = 0.15f,
            ["roughness"] = 0.45f,
            ["emissive"] = 0f
        });

        Assert.Equal(32, bytes.Length);
        Assert.Equal(0.2f, ReadSingle(bytes, 0));
        Assert.Equal(0.6f, ReadSingle(bytes, 4));
        Assert.Equal(1f, ReadSingle(bytes, 8));
        Assert.Equal(1f, ReadSingle(bytes, 12));
        Assert.Equal(0.15f, ReadSingle(bytes, 16));
        Assert.Equal(0.45f, ReadSingle(bytes, 20));
        Assert.Equal(0f, ReadSingle(bytes, 24));
    }

    [Fact]
    public void AlignsVec3To16BytesInTheUniformAddressSpace()
    {
        var fields = new[]
        {
            new ShaderStructField("offset", "vec3<f32>"),
            new ShaderStructField("value", "f32")
        };
        var bytes = UniformPacker.Pack(fields, new Dictionary<string, ShaderParameter>
        {
            ["offset"] = new Vector3(1f, 2f, 3f),
            ["value"] = 7f
        });

        // vec3 starts 16-aligned and spans 12 bytes; the following f32 fits
        // immediately after (12 % 4 == 0), matching WGSL's member rules.
        Assert.Equal(16, bytes.Length);
        Assert.Equal(1f, ReadSingle(bytes, 0));
        Assert.Equal(2f, ReadSingle(bytes, 4));
        Assert.Equal(3f, ReadSingle(bytes, 8));
        Assert.Equal(7f, ReadSingle(bytes, 12));
    }

    [Fact]
    public void PacksMatrix4WithA64ByteFootprint()
    {
        var fields = new[]
        {
            new ShaderStructField("model", "mat4x4<f32>"),
            new ShaderStructField("flag", "f32")
        };
        var bytes = UniformPacker.Pack(fields, new Dictionary<string, ShaderParameter>
        {
            ["model"] = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            ["flag"] = 1f
        });

        Assert.Equal(80, bytes.Length);
        // CreateTranslation stores the translation in M41/M42/M43 (offsets 48/52/56).
        Assert.Equal(1f, ReadSingle(bytes, 48));
        Assert.Equal(2f, ReadSingle(bytes, 52));
        Assert.Equal(3f, ReadSingle(bytes, 56));
        Assert.Equal(1f, ReadSingle(bytes, 64));
    }

    [Fact]
    public void MissingValuesArePackedAsZeros()
    {
        var bytes = UniformPacker.Pack(MaterialFields, new Dictionary<string, ShaderParameter>());

        Assert.Equal(32, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    private static float ReadSingle(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);
}
