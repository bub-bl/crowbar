using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// 3D listener: the position/axes of the listening camera, used by spatialized
/// sounds (<see cref="Audio.Play3D"/>) to compute inverse-distance attenuation
/// and equal-power panning. Doppler and HRTF are deliberately deferred (future
/// extensions: Steam Audio or C# binaural).
/// </summary>
public sealed class AudioListener
{
    /// <summary>Listener position (world units).</summary>
    public Vector3 Position;

    /// <summary>Listening direction (normalized by the computation).</summary>
    public Vector3 Forward = Vector3.UnitZ;

    /// <summary>Listener up vector (normalized by the computation).</summary>
    public Vector3 Up = Vector3.UnitY;

    /// <summary>
    /// Attenuation factor: <c>1 / (1 + distance * Rolloff)</c>. The larger the
    /// value, the faster the sound fades.
    /// </summary>
    public float Rolloff = 1f;

    /// <summary>
    /// Air absorption: spatialized sounds roll off high frequencies with
    /// distance (<c>cutoff = 20000 * exp(-distance * AirAbsorption)</c>).
    /// <c>0</c> (the default) disables the effect.
    /// </summary>
    public float AirAbsorption;

    /// <summary>Right vector, derived from <see cref="Forward"/> and <see cref="Up"/>.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

    /// <summary>Places the listener from a position and a look direction.</summary>
    public void SetOrientation(Vector3 position, Vector3 forward, Vector3 up)
    {
        Position = position;
        Forward = forward.LengthSquared() > 0f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        Up = up.LengthSquared() > 0f ? Vector3.Normalize(up) : Vector3.UnitY;
    }
}
