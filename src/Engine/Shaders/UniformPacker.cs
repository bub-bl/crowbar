using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Crowbar.Engine;

/// <summary>
/// Packs material parameter values into a uniform buffer. The per-field byte
/// offsets, sizes and alignments come from <see cref="ShaderStructField"/>
/// (populated by slangc's reflection sidecar), so the engine no longer
/// re-derives the std140 layout from type names — slangc is the single source
/// of truth and a layout change in the shader is picked up automatically.
/// </summary>
public static class UniformPacker
{
    /// <summary>Total size of the struct: the furthest field extent, rounded up to the struct's max alignment.</summary>
    public static int ComputeStructSize(IReadOnlyList<ShaderStructField> fields)
    {
        var size = 0;
        var maxAlignment = 1;
        foreach (var field in fields)
        {
            size = Math.Max(size, field.Offset + field.Size);
            maxAlignment = Math.Max(maxAlignment, field.Alignment);
        }

        return AlignUp(size, maxAlignment);
    }

    /// <summary>
    /// Packs the given values at their reflected offsets. Missing values are
    /// left as zeros; matrices are copied in System.Numerics storage order,
    /// which matches WGSL's column-major interpretation when consumed with
    /// column-vector multiplication.
    /// </summary>
    public static byte[] Pack(
        IReadOnlyList<ShaderStructField> fields,
        IReadOnlyDictionary<string, ShaderParameter> values)
    {
        var bytes = new byte[ComputeStructSize(fields)];
        foreach (var field in fields)
        {
            if (values.TryGetValue(field.Name, out var value))
                Write(bytes, field.Offset, value);
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
}
