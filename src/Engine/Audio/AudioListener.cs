using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Écouteur 3D : la position/les axes de la caméra d'écoute, utilisés par les
/// voix spatialisées (<see cref="Audio.Play3D"/>) pour calculer l'atténuation
/// inverse-distance et le panoramique equal-power. Le Doppler et le HRTF sont
/// volontairement différés (extensions futures : Steam Audio ou binaural C#).
/// </summary>
public sealed class AudioListener
{
    /// <summary>Position de l'écouteur (unités monde).</summary>
    public Vector3 Position;

    /// <summary>Direction d'écoute (normalisée par le calcul).</summary>
    public Vector3 Forward = Vector3.UnitZ;

    /// <summary>Haut de l'écouteur (normalisé par le calcul).</summary>
    public Vector3 Up = Vector3.UnitY;

    /// <summary>
    /// Facteur d'atténuation : <c>1 / (1 + distance * Rolloff)</c>. Plus la
    /// valeur est grande, plus le son décroît vite.
    /// </summary>
    public float Rolloff = 1f;

    /// <summary>Vecteur droit, dérivé de <see cref="Forward"/> et <see cref="Up"/>.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

    /// <summary>Place l'écouteur à partir d'une position et d'une direction de visée.</summary>
    public void SetOrientation(Vector3 position, Vector3 forward, Vector3 up)
    {
        Position = position;
        Forward = forward.LengthSquared() > 0f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        Up = up.LengthSquared() > 0f ? Vector3.Normalize(up) : Vector3.UnitY;
    }
}
