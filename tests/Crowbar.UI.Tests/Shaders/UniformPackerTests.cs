using System.Numerics;

namespace Crowbar.Engine.Tests;

public class UniformPackerTests
{
    // Offsets/sizes/alignments are what slangc's reflection reports for a
    // MaterialUniforms { vec4 color; float metallic; float roughness; float emissive; }
    // struct under std140: color spans 0..16, then the three floats pack
    // back-to-back at 16/20/24.
    private static readonly ShaderStructField[] MaterialFields =
    [
        new("color", "vec4<f32>", 0, 16, 16),
        new("metallic", "f32", 16, 4, 4),
        new("roughness", "f32", 20, 4, 4),
        new("emissive", "f32", 24, 4, 4)
    ];

    [Fact]
    public void PacksValuesAtTheirReflectedOffsets()
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
    public void StructSizeRoundsTheFurthestFieldExtentUpToMaxAlignment()
    {
        // std140: vec3 spans bytes 0..12 aligned to 16; the following f32 sits
        // at 12 (4-aligned), so the struct is 16 bytes despite the tail gap.
        var fields = new[]
        {
            new ShaderStructField("offset", "vec3<f32>", 0, 12, 16),
            new ShaderStructField("value", "f32", 12, 4, 4)
        };
        var bytes = UniformPacker.Pack(fields, new Dictionary<string, ShaderParameter>
        {
            ["offset"] = new Vector3(1f, 2f, 3f),
            ["value"] = 7f
        });

        Assert.Equal(16, bytes.Length);
        Assert.Equal(1f, ReadSingle(bytes, 0));
        Assert.Equal(2f, ReadSingle(bytes, 4));
        Assert.Equal(3f, ReadSingle(bytes, 8));
        Assert.Equal(7f, ReadSingle(bytes, 12));
    }

    [Fact]
    public void PacksMatrixAtItsReflectedOffset()
    {
        var fields = new[]
        {
            new ShaderStructField("model", "mat4x4<f32>", 0, 64, 16),
            new ShaderStructField("flag", "f32", 64, 4, 4)
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
