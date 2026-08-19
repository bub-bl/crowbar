using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Handle to a playing voice, returned by <see cref="AudioSystem.Play"/> and
/// <see cref="Audio.Play"/>. It is a lightweight <see cref="ValueType"/>: it
/// carries the slot index, its generation and a reference to the owning
/// <see cref="AudioSystem"/>. Every method enqueues a command into the SPSC
/// queue; they are safe from any game thread (including a scripted gamemode).
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

    /// <summary>Invalid handle (never played).</summary>
    public static VoiceHandle Invalid => default;

    /// <summary>True as long as the slot still matches this generation.</summary>
    public bool IsValid => System is not null && System.IsVoiceAlive(Slot, Generation);

    /// <summary>Stops the voice (the slot becomes free at the end of the current block).</summary>
    public void Stop() => System?.EnqueueStop(Slot, Generation);

    /// <summary>Sets the volume (linear, 0..infinity, 1 = unity).</summary>
    public void SetVolume(float volume) => System?.EnqueueSetVolume(Slot, Generation, volume);

    /// <summary>Sets the pitch (1 = normal speed, 2 = one octave up).</summary>
    public void SetPitch(float pitch) => System?.EnqueueSetPitch(Slot, Generation, pitch);

    /// <summary>Sets the panning (-1 = left, +1 = right).</summary>
    public void SetPan(float pan) => System?.EnqueueSetPan(Slot, Generation, pan);

    /// <summary>Glides the volume toward <paramref name="volume"/> over <paramref name="duration"/> seconds.</summary>
    public void FadeTo(float volume, float duration) => System?.EnqueueFadeTo(Slot, Generation, volume, duration);

    /// <summary>Repositions the 3D source (see <see cref="Audio.Play3D"/>).</summary>
    public void SetPosition(Vector3 position) => System?.EnqueueSetPosition(Slot, Generation, position);

    /// <summary>Writes a parameter of an effect in the voice's effect chain.</summary>
    public void SetEffectParameter(int effectIndex, int parameterIndex, float value) =>
        System?.EnqueueSetEffectParameter(Slot, Generation, effectIndex, parameterIndex, value);

    /// <summary>Appends an effect to the voice's chain (a new instance per voice).</summary>
    public void AddEffect(IAudioEffect effect) => System?.EnqueueAddEffect(Slot, Generation, effect);
}
