using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Poignée vers une voix jouée, retournée par <see cref="AudioSystem.Play"/> et
/// <see cref="Audio.Play"/>. C'est un <see cref="ValueType"/> léger : il porte
/// l'index du slot, sa génération et une référence vers le
/// <see cref="AudioSystem"/> propriétaire. Toutes les méthodes envoient une
/// commande à la file SPSC ; elles sont sûres depuis n'importe quel thread de
/// jeu (y compris un gamemode scripté).
/// </summary>
public readonly struct VoiceHandle
{
    internal readonly AudioSystem? System;
    internal readonly int Slot;
    internal readonly int Generation;

    internal VoiceHandle(AudioSystem? system, int slot, int generation)
    {
        System = system;
        Slot = slot;
        Generation = generation;
    }

    /// <summary>Poignée invalide (jamais jouée).</summary>
    public static VoiceHandle Invalid => default;

    /// <summary>Vrai tant que le slot correspond toujours à cette génération.</summary>
    public bool IsValid => System is not null && System.IsVoiceAlive(Slot, Generation);

    /// <summary>Arrête la voix (le slot redevient libre à la fin du bloc courant).</summary>
    public void Stop() => System?.EnqueueStop(Slot, Generation);

    /// <summary>Règle le volume (linéaire, 0..∞, 1 = unité).</summary>
    public void SetVolume(float volume) => System?.EnqueueSetVolume(Slot, Generation, volume);

    /// <summary>Règle le pitch (1 = vitesse normale, 2 = une octave au-dessus).</summary>
    public void SetPitch(float pitch) => System?.EnqueueSetPitch(Slot, Generation, pitch);

    /// <summary>Règle le panoramique (-1 = gauche, +1 = droite).</summary>
    public void SetPan(float pan) => System?.EnqueueSetPan(Slot, Generation, pan);

    /// <summary>Fait glisser le volume vers <paramref name="volume"/> sur <paramref name="duration"/> secondes.</summary>
    public void FadeTo(float volume, float duration) => System?.EnqueueFadeTo(Slot, Generation, volume, duration);

    /// <summary>Repositionne la source 3D (voir <see cref="Audio.Play3D"/>).</summary>
    public void SetPosition(Vector3 position) => System?.EnqueueSetPosition(Slot, Generation, position);

    /// <summary>Écrit un paramètre d'un effet de la chaîne de la voix.</summary>
    public void SetEffectParameter(int effectIndex, int parameterIndex, float value) =>
        System?.EnqueueSetEffectParameter(Slot, Generation, effectIndex, parameterIndex, value);

    /// <summary>Ajoute un effet en fin de chaîne de la voix (nouvelle instance par voix).</summary>
    public void AddEffect(IAudioEffect effect) => System?.EnqueueAddEffect(Slot, Generation, effect);
}
