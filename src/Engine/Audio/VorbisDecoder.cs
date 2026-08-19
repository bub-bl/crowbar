using NVorbis;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Ogg/Vorbis decoder backed by NVorbis (fully managed, MIT). The container is
/// read from a <c>byte[]</c> loaded once, and the output is normalized to
/// interleaved stereo <see cref="float"/>, like <see cref="WavDecoder"/>.
/// Decoding stays allocation-free on the DSP thread: the scratch buffer is
/// pre-allocated and only the read position changes per call.
/// </summary>
public sealed class VorbisDecoder : IAudioDecoder
{
    private const int ScratchFrames = 2048;

    private readonly MemoryStream _stream;
    private readonly VorbisReader _reader;
    private readonly int _sourceChannels;
    private readonly float[] _scratch;
    private long _position;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames { get; }
    public long Position => _position;

    public VorbisDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _stream = new MemoryStream(data, writable: false);
        _reader = new VorbisReader(_stream, closeOnDispose: false);
        SampleRate = _reader.SampleRate;
        _sourceChannels = _reader.Channels is 1 or 2 ? _reader.Channels : 2;
        TotalFrames = _reader.TotalSamples > 0 ? _reader.TotalSamples : -1;
        _scratch = new float[ScratchFrames * _sourceChannels];
    }

    public int Read(Span<float> destination)
    {
        var frames = destination.Length / 2;
        var produced = 0;

        while (produced < frames)
        {
            var count = Math.Min(frames - produced, ScratchFrames);
            var read = _reader.ReadSamples(_scratch.AsSpan(0, count * _sourceChannels));
            if (read <= 0)
                break;

            var readFrames = read / _sourceChannels;
            for (var i = 0; i < readFrames; i++)
            {
                var left = _scratch[i * _sourceChannels];
                var right = _sourceChannels == 2 ? _scratch[i * _sourceChannels + 1] : left;
                destination[(produced + i) * 2] = left;
                destination[(produced + i) * 2 + 1] = right;
            }

            produced += readFrames;
        }

        _position += produced;
        return produced;
    }

    public void Seek(long frame)
    {
        var clamped = TotalFrames >= 0 ? Math.Clamp(frame, 0, TotalFrames) : Math.Max(0, frame);
        _reader.SeekTo(clamped, SeekOrigin.Begin);
        // Read the position back so it stays consistent with what the decoder
        // will actually deliver next (Vorbis seek is sample-accurate, but the
        // provider may clamp or snap near the stream boundaries).
        _position = _reader.SamplePosition;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
