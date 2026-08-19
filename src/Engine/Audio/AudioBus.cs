namespace Crowbar.Engine.Audio;

/// <summary>
/// Node of the mixing tree: the master aggregates its child buses (Music, SFX,
/// UI, Voice), each bus sums the sounds routed to it then applies its effect
/// chain. Gain, mute and solo are volatile fields read/written lock-free from
/// the game thread.
///
/// The effect chain is configured before <see cref="AudioSystem.Start"/> (the
/// parameters themselves stay hot-modifiable via
/// <see cref="SetEffectParameter"/>).
/// </summary>
public sealed class AudioBus
{
    private const int MaxEffects = 8;

    private readonly IAudioEffect?[] _effects = new IAudioEffect?[MaxEffects];
    private int _effectCount;
    private volatile float _peak;
    private volatile float _rms;

    public string Name { get; }

    /// <summary>Linear gain of the bus (1 = unity).</summary>
    public volatile float Gain = 1f;

    /// <summary>Cuts the bus (zero output) without destroying its sounds.</summary>
    public volatile bool Mute;

    /// <summary>Solos this bus (the other buses are cut).</summary>
    public volatile bool Solo;

    /// <summary>Accumulation buffer of the current block (frames x 2, stereo).</summary>
    internal float[] Accumulator { get; }

    /// <summary>Peak of the last rendered block (0..1), smoothed.</summary>
    public float Peak => _peak;

    /// <summary>RMS of the last rendered block (0..1), smoothed.</summary>
    public float Rms => _rms;

    public int EffectCount => _effectCount;

    internal AudioBus(string name, int blockFrames)
    {
        Name = name;
        Accumulator = new float[blockFrames * 2];
    }

    /// <summary>Appends an effect to the chain (before <see cref="AudioSystem.Start"/>).</summary>
    public void AddEffect(IAudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (_effectCount >= MaxEffects)
            throw new InvalidOperationException($"A bus accepts at most {MaxEffects} effects.");
        _effects[_effectCount++] = effect;
    }

    /// <summary>Returns the effect at <paramref name="index"/>, or null.</summary>
    public IAudioEffect? GetEffect(int index) => index >= 0 && index < _effectCount ? _effects[index] : null;

    /// <summary>Writes a parameter of an effect in the chain (atomic, lock-free).</summary>
    public void SetEffectParameter(int effectIndex, int parameterIndex, float value) =>
        _effects[effectIndex]?.SetParameter(parameterIndex, value);

    internal void Clear() => Array.Clear(Accumulator);

    internal void ProcessEffects(int frames, double time)
    {
        if (_effectCount == 0)
            return;

        var context = new AudioEffectContext(AudioSystem.SampleRate, AudioSystem.Channels, frames, time);
        for (var i = 0; i < _effectCount; i++)
            _effects[i]!.Process(Accumulator, context);
    }

    internal void SetMeters(float peak, float rms, float smoothing)
    {
        _peak = Math.Max(peak, _peak * smoothing);
        _rms = rms * (1f - smoothing) + _rms * smoothing;
    }
}
