using NLayer;

namespace Crowbar.Engine.Audio;

/// <summary>
/// MP3 decoder backed by NLayer (MPEG 1/2, layers 1/2/3, fully managed, MIT).
/// The file is read from a <c>byte[]</c> loaded once, and the output is
/// normalized to interleaved stereo <see cref="float"/>, like
/// <see cref="WavDecoder"/>. Decoding stays allocation-free on the DSP thread:
/// the scratch buffer is pre-allocated and only the read position changes per
/// call.
/// </summary>
public sealed class Mp3Decoder : IAudioDecoder
{
    private const int ScratchFrames = 2048;

    private readonly MemoryStream _stream;
    private readonly MpegFile _reader;
    private readonly int _sourceChannels;
    private readonly float[] _scratch;
    private long _position;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames { get; }
    public long Position => _position;

    public Mp3Decoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _stream = new MemoryStream(data, writable: false);
        _reader = new MpegFile(_stream);
        SampleRate = _reader.SampleRate;
        _sourceChannels = _reader.Channels is 1 or 2 ? _reader.Channels : 2;
        // NLayer.Length is the decoded payload in bytes of float: convert to frames.
        TotalFrames = _reader.Length >= 0 ? _reader.Length / (sizeof(float) * _sourceChannels) : -1;
        _scratch = new float[ScratchFrames * _sourceChannels];
    }

    public int Read(Span<float> destination)
    {
        var frames = destination.Length / 2;
        var produced = 0;

        while (produced < frames)
        {
            var count = Math.Min(frames - produced, ScratchFrames);
            var read = _reader.ReadSamples(_scratch, 0, count * _sourceChannels);
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
        if (!_reader.CanSeek)
            throw new NotSupportedException("This MP3 stream is not seekable.");

        var clamped = TotalFrames >= 0 ? Math.Clamp(frame, 0, TotalFrames) : Math.Max(0, frame);
        _reader.Position = clamped * _sourceChannels * sizeof(float);
        // NLayer lands on a frame boundary (its Position is in float bytes), so
        // read the effective position back to keep the frame counter in sync.
        _position = _reader.Position / (sizeof(float) * _sourceChannels);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
