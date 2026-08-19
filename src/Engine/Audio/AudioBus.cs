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
    private readonly List<(AudioBus Target, float Level)> _sends = [];

    public string Name { get; }

    /// <summary>Linear gain of the bus (1 = unity).</summary>
    public volatile float Gain = 1f;

    /// <summary>Cuts the bus (zero output) without destroying its sounds.</summary>
    public volatile bool Mute;

    /// <summary>Solos this bus (the other buses are cut).</summary>
    public volatile bool Solo;

    /// <summary>Accumulation buffer of the current block (frames x 2, stereo).</summary>
    internal float[] Accumulator { get; }

    /// <summary>Smoothed output gain, ramped over a block when <see cref="Gain"/> changes (avoids stepping).</summary>
    internal float SmoothedGain = 1f;

    /// <summary>
    /// Bus to duck (lower its gain) while this bus produces sound. Configure it
    /// with <see cref="Duck"/> so dialogue ducking lowers the music automatically.
    /// </summary>
    public AudioBus? DuckTarget;

    /// <summary>Level the ducked bus drops to while this bus is audible (0..1).</summary>
    public float DuckAmount = 0.3f;

    /// <summary>How fast the duck kicks in (seconds).</summary>
    public float DuckAttackSeconds = 0.05f;

    /// <summary>How fast the duck releases (seconds).</summary>
    public float DuckReleaseSeconds = 0.5f;

    /// <summary>Smoothed duck level of this bus (1 = no duck, <see cref="DuckAmount"/> = ducking).</summary>
    internal float DuckLevel = 1f;

    /// <summary>Accumulated duck gain applied to this bus this block (product of ducking sources).</summary>
    internal float DuckGain = 1f;

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

    /// <summary>
    /// Routes a post-effects copy of this bus to <paramref name="target"/> at
    /// <paramref name="level"/> (auxiliary send, e.g. a reverb bus). Cycles are
    /// rejected. Configure before <see cref="AudioSystem.Start"/>.
    /// </summary>
    public void AddSend(AudioBus target, float level)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this))
            throw new ArgumentException("A bus cannot send to itself.", nameof(target));
        if (level < 0f)
            throw new ArgumentOutOfRangeException(nameof(level), "Send levels must be non-negative.");
        if (Targets(target, this))
            throw new ArgumentException("Sending to this target would create a cycle in the send graph.", nameof(target));

        _sends.Add((target, level));
    }

    /// <summary>Makes this bus duck <paramref name="target"/> while it is audible.</summary>
    public void Duck(AudioBus? target, float amount = 0.3f, float attackSeconds = 0.05f, float releaseSeconds = 0.5f)
    {
        DuckTarget = target;
        DuckAmount = Math.Clamp(amount, 0f, 1f);
        DuckAttackSeconds = Math.Max(0f, attackSeconds);
        DuckReleaseSeconds = Math.Max(0f, releaseSeconds);
    }

    internal List<(AudioBus Target, float Level)> Sends => _sends;

    private static bool Targets(AudioBus from, AudioBus target)
    {
        // True when a send chain reaches `target` starting from `from`.
        if (ReferenceEquals(from, target))
            return true;
        foreach (var (next, _) in from._sends)
        {
            if (Targets(next, target))
                return true;
        }
        return false;
    }

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
