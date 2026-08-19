using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Handle to a playing sound, returned by <see cref="AudioSystem.Play"/> and
/// <see cref="Audio.Play"/>. It is a lightweight <see cref="ValueType"/>: it
/// carries the slot index, its generation and a reference to the owning
/// <see cref="AudioSystem"/>. Every method enqueues a command into the SPSC
/// queue; they are safe from any game thread (including a scripted gamemode).
/// </summary>
public readonly struct SoundHandle
{
    internal readonly AudioSystem? System;
    internal readonly int Slot;
    internal readonly int Generation;

    internal SoundHandle(AudioSystem? system, int slot, int generation)
    {
        System = system;
        Slot = slot;
        Generation = generation;
    }

    /// <summary>Invalid handle (never played).</summary>
    public static SoundHandle Invalid => default;

    /// <summary>True as long as the slot still matches this generation.</summary>
    public bool IsValid => System is not null && System.IsSoundAlive(Slot, Generation);

    /// <summary>Stops the sound (the slot becomes free at the end of the current block).</summary>
    public void Stop() => System?.EnqueueStop(Slot, Generation);

    /// <summary>Pauses the sound: the read head freezes and the last frame keeps playing.</summary>
    public void Pause() => System?.EnqueuePause(Slot, Generation);

    /// <summary>Resumes a paused sound.</summary>
    public void Resume() => System?.EnqueueResume(Slot, Generation);

    /// <summary>True while the sound is paused (live read, safe from any thread).</summary>
    public bool IsPaused => System?.IsSoundPaused(Slot, Generation) ?? false;

    /// <summary>Current playback position in seconds (0 for an unknown/invalid handle).</summary>
    public float PositionSeconds => System?.GetSoundPositionSeconds(Slot, Generation) ?? 0f;

    /// <summary>Known source duration in seconds, or -1 when unknown (streams).</summary>
    public float DurationSeconds => System?.GetSoundDurationSeconds(Slot, Generation) ?? -1f;

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

    /// <summary>Sets the source velocity (used for Doppler on spatialized sounds).</summary>
    public void SetVelocity(Vector3 velocity) => System?.EnqueueSetVelocity(Slot, Generation, velocity);

    /// <summary>Ramps the volume to zero over <paramref name="duration"/> seconds, then stops.</summary>
    public void FadeToStop(float duration) => System?.EnqueueFadeStop(Slot, Generation, duration);

    /// <summary>Writes a parameter of an effect in the sound's effect chain.</summary>
    public void SetEffectParameter(int effectIndex, int parameterIndex, float value) =>
        System?.EnqueueSetEffectParameter(Slot, Generation, effectIndex, parameterIndex, value);

    /// <summary>Writes a parameter by name (e.g. <c>"Wet"</c>, <c>"Frequency"</c>).</summary>
    public void SetEffectParameter(int effectIndex, string name, float value) =>
        System?.EnqueueSetEffectParameter(Slot, Generation, effectIndex, name, value);

    /// <summary>Appends an effect to the sound's chain (a new instance per sound).</summary>
    public void AddEffect(IAudioEffect effect) => System?.EnqueueAddEffect(Slot, Generation, effect);
}
