using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// Blends a selector value across the active instances of one effect type:
/// the volume instances the camera is inside (weighted by position) plus the
/// global/camera instance at weight 1. Numeric and vector values blend
/// arithmetically; enums and other types take the strongest instance.
/// </summary>
internal static class PostProcessBlender
{
    public static U Blend<U>(
        IReadOnlyList<PostProcessEntry> entries,
        PostProcess driver,
        Func<PostProcess, U> selector,
        U defaultValue,
        bool onlyLerpBetweenVolumes)
    {
        var volumes = entries.Where(entry => !entry.IsGlobal && entry.Weight > 0f).ToList();
        if (onlyLerpBetweenVolumes)
            return volumes.Count == 0 ? defaultValue : BlendValues(volumes, selector);

        if (volumes.Count == 0)
            return selector(driver);

        var candidates = volumes.ToList();
        // The driver participates at full weight unless it is already a
        // (weighted) volume instance.
        if (!candidates.Any(entry => ReferenceEquals(entry.Instance, driver)))
            candidates.Insert(0, new PostProcessEntry(driver, 1f, IsGlobal: true));
        return BlendValues(candidates, selector);
    }

    private static U BlendValues<U>(IReadOnlyList<PostProcessEntry> entries, Func<PostProcess, U> selector)
    {
        var total = entries.Sum(entry => entry.Weight);
        if (total <= 0f)
            return default!;

        var dominant = entries.OrderByDescending(entry => entry.Weight).First().Instance;
        return selector(dominant) switch
        {
            float => (U)(object)(entries.Sum(entry => (float)(object)selector(entry.Instance)! * entry.Weight) / total),
            double => (U)(object)(entries.Sum(entry => (double)(object)selector(entry.Instance)! * entry.Weight) / total),
            int => (U)(object)(int)MathF.Round(entries.Sum(entry => (int)(object)selector(entry.Instance)! * entry.Weight) / total),
            uint => (U)(object)(uint)MathF.Round(entries.Sum(entry => (uint)(object)selector(entry.Instance)! * entry.Weight) / total),
            bool => (U)(object)(entries.Sum(entry => (bool)(object)selector(entry.Instance)! ? entry.Weight : 0f) >= total * 0.5f),
            Vector2 => (U)(object)(entries.Aggregate(Vector2.Zero,
                (acc, entry) => acc + (Vector2)(object)selector(entry.Instance)! * entry.Weight) / total),
            Vector3 => (U)(object)(entries.Aggregate(Vector3.Zero,
                (acc, entry) => acc + (Vector3)(object)selector(entry.Instance)! * entry.Weight) / total),
            Vector4 => (U)(object)(entries.Aggregate(Vector4.Zero,
                (acc, entry) => acc + (Vector4)(object)selector(entry.Instance)! * entry.Weight) / total),
            // Enums and other types: the strongest instance wins.
            _ => (U)(object)selector(dominant)!
        };
    }
}
