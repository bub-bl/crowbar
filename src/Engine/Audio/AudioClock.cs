namespace Crowbar.Engine.Audio;

/// <summary>
/// Monotonic <see cref="long"/> sample counter that acts as the audio engine's
/// time base: it links real-time rendering, offline rendering (tests/export)
/// and future image/audio synchronization. The DSP thread advances it by
/// <c>frames</c> samples on every rendered block; the game thread only reads it.
/// </summary>
public sealed class AudioClock
{
    /// <summary>Engine sample rate (Hz).</summary>
    public int SampleRate { get; }

    private long _samples;

    public AudioClock(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    /// <summary>Current position, in samples (frames) since startup.</summary>
    public long PositionSamples => Interlocked.Read(ref _samples);

    /// <summary>Current position, in seconds.</summary>
    public double TimeSeconds => PositionSamples / (double)SampleRate;

    /// <summary>
    /// Advances the counter by <paramref name="frames"/> samples. Called only by
    /// the DSP thread (or by an offline render).
    /// </summary>
    public void Advance(int frames) => Interlocked.Add(ref _samples, frames);

    /// <summary>Resets the counter to zero.</summary>
    public void Reset() => Interlocked.Exchange(ref _samples, 0);
}
