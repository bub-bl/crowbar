namespace Crowbar.Engine.Audio;

/// <summary>
/// Execution context passed to every <see cref="IAudioEffect.Process"/>: it
/// describes the current block (format, size, time) without any backend
/// dependency. Effects therefore remain pure C#, testable offline.
/// </summary>
public readonly struct AudioEffectContext
{
    /// <summary>Sample rate (Hz).</summary>
    public readonly int SampleRate;

    /// <summary>Channel count (the engine works in interleaved stereo).</summary>
    public readonly int Channels;

    /// <summary>Number of frames (samples per channel) in the buffer.</summary>
    public readonly int Frames;

    /// <summary>Audio time at the start of the block (seconds), since engine startup.</summary>
    public readonly double TimeSeconds;

    public AudioEffectContext(int sampleRate, int channels, int frames, double timeSeconds)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Frames = frames;
        TimeSeconds = timeSeconds;
    }
}

/// <summary>
/// Homegrown DSP effect, in pure C#: it processes an interleaved stereo block in
/// place. Implementations keep their internal state (delay lines, coefficients,
/// envelopes) in buffers allocated at construction time, so that
/// <see cref="Process"/> allocates nothing in steady state on the DSP thread.
///
/// Parameters are volatile <c>float</c> values: writing them from the game
/// thread is atomic and lock-free, and the effect applies whatever smoothing is
/// needed to avoid artifacts (zipper noise) on the DSP side.
/// </summary>
public interface IAudioEffect
{
    /// <summary>Number of parameters exposed by <see cref="GetParameter"/>.</summary>
    int ParameterCount { get; }

    /// <summary>Processes the interleaved stereo block in place.</summary>
    void Process(Span<float> buffer, AudioEffectContext context);

    /// <summary>Reads the value of parameter <paramref name="index"/>.</summary>
    float GetParameter(int index);

    /// <summary>Writes the value of parameter <paramref name="index"/> (atomic, lock-free).</summary>
    void SetParameter(int index, float value);

    /// <summary>Resets internal state (delay lines, envelopes, ...).</summary>
    void Reset();
}
