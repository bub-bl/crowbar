namespace Crowbar.Engine;

/// <summary>
/// A post-process whose settings can be blended across multiple instances: the
/// global/camera one plus any <see cref="PostProcessVolume"/> instances the
/// camera is inside. Inside <see cref="PostProcess.Render"/>, read every
/// tunable through <see cref="GetWeighted{T}"/> instead of the property
/// directly — the returned value is the volume-weighted blend. The type
/// parameter is the derived type itself (the CRTP pattern, like s&amp;box's
/// <c>BasePostProcess&lt;T&gt;</c>).
/// </summary>
public abstract class BasePostProcess<T> : PostProcess where T : BasePostProcess<T>
{
    /// <summary>
    /// The blended value of <paramref name="selector"/> across the active
    /// instances of this effect: every volume instance the camera is inside
    /// (weighted by camera position) plus this component at weight 1. With no
    /// volume at all, returns this component's own value; with
    /// <paramref name="onlyLerpBetweenVolumes"/> the component's own value is
    /// excluded and <paramref name="defaultValue"/> is returned when no volume
    /// contributes. Only meaningful while rendering (inside
    /// <see cref="PostProcess.Render"/>).
    /// </summary>
    protected U GetWeighted<U>(Func<T, U> selector, U defaultValue = default!, bool onlyLerpBetweenVolumes = false)
    {
        var context = PostProcessContext.Current;
        if (context is null || context.Entries.Count == 0)
            return selector((T)(object)this);
        return PostProcessBlender.Blend(context.Entries, this,
            instance => selector((T)instance), defaultValue, onlyLerpBetweenVolumes);
    }
}
