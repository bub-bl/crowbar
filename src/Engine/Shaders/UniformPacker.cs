using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Crowbar.Engine;

/// <summary>
/// Packs material parameter values into the byte layout WGSL expects for a
/// uniform buffer: the uniform address space requires 16-byte alignment,
/// vec3 occupies 12 bytes but is aligned to 16, mat4 is column-major with a
/// 64-byte footprint. The layout is derived from the shader's own struct
/// fields, so the WGSL remains the single source of truth — a new field in
/// the shader is picked up without touching any C# struct.
/// </summary>
public static class UniformPacker
{
    /// <summary>Alignment of a WGSL type in the uniform address space.</summary>
    public static int GetAlignment(string wgslType) => Normalize(wgslType) switch
    {
        "f32" or "i32" or "u32" or "bool" => 4,
        "vec2" => 8,
        _ => 16
    };

    /// <summary>Size in bytes of a WGSL type (before struct tail padding).</summary>
    public static int GetSize(string wgslType) => Normalize(wgslType) switch
    {
        "f32" or "i32" or "u32" or "bool" => 4,
        "vec2" => 8,
        "vec3" => 12,
        "vec4" => 16,
        "mat4" => 64,
        _ => 16
    };

    /// <summary>Total size of the struct, including per-field padding and the tail.</summary>
    public static int ComputeStructSize(IReadOnlyList<ShaderStructField> fields)
    {
        var offset = 0;
        var maxAlignment = 1;
        foreach (var field in fields)
        {
            var alignment = GetAlignment(field.Type);
            maxAlignment = Math.Max(maxAlignment, alignment);
            offset = AlignUp(offset, alignment) + GetSize(field.Type);
        }

        return AlignUp(offset, maxAlignment);
    }

    /// <summary>
    /// Packs the given values into a byte buffer matching the WGSL struct
    /// layout. Missing values are left as zeros; matrices are copied in
    /// System.Numerics storage order, which matches WGSL's column-major
    /// interpretation when consumed with column-vector multiplication.
    /// </summary>
    public static byte[] Pack(
        IReadOnlyList<ShaderStructField> fields,
        IReadOnlyDictionary<string, ShaderParameter> values)
    {
        var bytes = new byte[ComputeStructSize(fields)];
        var offset = 0;
        foreach (var field in fields)
        {
            offset = AlignUp(offset, GetAlignment(field.Type));
            if (values.TryGetValue(field.Name, out var value))
                Write(bytes, offset, value);
            offset += GetSize(field.Type);
        }

        return bytes;
    }

    private static void Write(byte[] bytes, int offset, ShaderParameter value)
    {
        switch (value)
        {
            case float f:
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), f);
                break;
            case int i:
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), i);
                break;
            case uint u:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), u);
                break;
            case bool b:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), b ? 1u : 0u);
                break;
            case Vector2 v:
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), v.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4, 4), v.Y);
                break;
            case Vector3 v:
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), v.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4, 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8, 4), v.Z);
                break;
            case Vector4 v:
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), v.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4, 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8, 4), v.Z);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 12, 4), v.W);
                break;
            case Matrix4x4 matrix:
                WriteMatrix(bytes, offset, matrix);
                break;
        }
    }

    private static void WriteMatrix(byte[] bytes, int offset, Matrix4x4 matrix)
    {
        var source = MemoryMarshal.CreateSpan(ref matrix.M11, 16);
        MemoryMarshal.AsBytes(source).CopyTo(bytes.AsSpan(offset, 64));
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static string Normalize(string wgslType) => wgslType switch
    {
        "vec2<f32>" => "vec2",
        "vec3<f32>" => "vec3",
        "vec4<f32>" => "vec4",
        "mat4x4<f32>" => "mat4",
        _ => wgslType
    };
}
