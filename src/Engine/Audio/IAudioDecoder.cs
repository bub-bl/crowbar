namespace Crowbar.Engine.Audio;

/// <summary>
/// Homegrown audio decoder: it produces interleaved stereo <see cref="float"/>
/// samples from a byte buffer loaded once. The decoder keeps its own read
/// position, so the DSP thread never re-reads a system stream:
/// <see cref="Read"/> must not allocate.
///
/// The provided implementations are <see cref="WavDecoder"/> (RIFF/WAVE) and
/// <see cref="AiffDecoder"/> (FORM/AIFF). OGG/Vorbis (NVorbis) and MP3 (NLayer)
/// will plug in here later, behind the same interface — see
/// <see cref="AudioDecoderFactory"/>.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Sample rate (Hz).</summary>
    int SampleRate { get; }

    /// <summary>Channel count of the stream (the engine normalizes to stereo).</summary>
    int Channels { get; }

    /// <summary>Total number of frames, or <c>-1</c> when unknown.</summary>
    long TotalFrames { get; }

    /// <summary>Current position, in frames.</summary>
    long Position { get; }

    /// <summary>
    /// Decodes up to <c>destination.Length / 2</c> frames into interleaved
    /// stereo. Returns the number of frames actually decoded (0 at end of stream).
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Repositions the read cursor at <paramref name="frame"/>.</summary>
    void Seek(long frame);
}
