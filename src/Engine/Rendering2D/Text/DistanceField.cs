namespace Crowbar.Engine.Rendering2D;

/// <summary>
/// Exact signed distance field from a binary mask, used to build the glyph
/// atlas. Distances are positive inside the mask, negative outside, with zero
/// on the boundary between inside/outside pixels. The two-pass 1D Euclidean
/// distance transform (Felzenszwalb &amp; Huttenlocher) runs in O(width·height).
/// </summary>
internal static class DistanceField
{
    private const float Inf = 1e10f;

    /// <summary>
    /// Computes the signed distance for every cell of a binary mask
    /// (<paramref name="inside"/>, row-major, length width·height). The result
    /// is in cell units; an inside cell adjacent to an outside cell is +0.5
    /// (the boundary sits half a cell away from the cell center).
    /// </summary>
    public static float[] Signed(bool[] inside, int width, int height)
    {
        if (inside.Length != width * height)
            throw new ArgumentException("Mask length does not match width * height.", nameof(inside));

        var toOutside = Squared(inside, width, height, sourceIsOutside: true);
        var toInside = Squared(inside, width, height, sourceIsOutside: false);

        var result = new float[inside.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = inside[i]
                ? MathF.Sqrt(toOutside[i]) - 0.5f
                : -(MathF.Sqrt(toInside[i]) - 0.5f);
        return result;
    }

    /// <summary>
    /// Squared Euclidean distance from every cell to the nearest "source" cell
    /// (0 at a source). Source cells are <c>!inside</c> when
    /// <paramref name="sourceIsOutside"/>, otherwise <c>inside</c>.
    /// </summary>
    private static float[] Squared(bool[] inside, int width, int height, bool sourceIsOutside)
    {
        var f = new float[inside.Length];
        for (var i = 0; i < f.Length; i++)
        {
            var isSource = sourceIsOutside ? !inside[i] : inside[i];
            f[i] = isSource ? 0f : Inf;
        }

        var row = new float[f.Length];
        var col = new float[f.Length];
        var line = Math.Max(width, height);
        var v = new int[line];
        var z = new float[line + 1];

        // Pass 1: 1D transform along each row.
        for (var y = 0; y < height; y++)
            Edt1D(f, row, y * width, 1, width, v, z);
        // Pass 2: 1D transform along each column of the row-transformed result.
        for (var x = 0; x < width; x++)
            Edt1D(row, col, x, width, height, v, z);
        return col;
    }

    /// <summary>
    /// 1D squared distance transform over <paramref name="length"/> elements of
    /// <paramref name="f"/> at <paramref name="offset"/> with the given
    /// <paramref name="stride"/>, writing into <paramref name="d"/>.
    /// </summary>
    private static void Edt1D(float[] f, float[] d, int offset, int stride, int length, int[] v, float[] z)
    {
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (var q = 1; q < length; q++)
        {
            var s = Intersect(f, offset, stride, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, offset, stride, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < length; q++)
        {
            while (z[k + 1] < q)
                k++;
            var dv = q - v[k];
            d[offset + q * stride] = dv * dv + f[offset + v[k] * stride];
        }
    }

    /// <summary>
    /// Intersection of the parabolas from indices <paramref name="q"/> and
    /// <paramref name="v"/>. Returns +infinity when both source values are at
    /// infinity (so an "all far" line degrades gracefully instead of NaN).
    /// </summary>
    private static float Intersect(float[] f, int offset, int stride, int q, int v)
    {
        var fq = f[offset + q * stride];
        var fv = f[offset + v * stride];
        if (fq >= Inf && fv >= Inf)
            return float.PositiveInfinity;
        return (fq + q * q - fv - v * v) / (2f * q - 2f * v);
    }
}
