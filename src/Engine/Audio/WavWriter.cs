using System.Buffers.Binary;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Hand-written WAV writer (16-bit PCM, stereo): it writes a provisional header,
/// then samples as they come, and finalizes the sizes on close. The underlying
/// stream is provided by the caller (project file, memory, network); the float
/// -> int16 conversion happens in a reused buffer, with no allocation in steady
/// state.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly byte[] _scratch;
    private long _frames;
    private bool _finalized;

    public WavWriter(Stream stream, int sampleRate, int channels = 2, int bitsPerSample = 16)
    {
        if (bitsPerSample != 16)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit PCM is written for now.");
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _sampleRate = sampleRate;
        _channels = channels;
        _scratch = new byte[AudioSystem.BlockSize * 2 * 2];

        WriteHeader(0);
    }

    /// <summary>Number of frames written so far.</summary>
    public long Frames => _frames;

    /// <summary>
    /// Writes a block of interleaved <see cref="float"/> samples, converted to
    /// signed 16-bit PCM.
    /// </summary>
    public void WriteInterleaved(ReadOnlySpan<float> interleaved)
    {
        if (_finalized)
            throw new InvalidOperationException("The WAV is already finalized.");

        var offset = 0;
        while (offset < interleaved.Length)
        {
            var count = Math.Min(interleaved.Length - offset, _scratch.Length / 2);
            var chunk = interleaved.Slice(offset, count);

            for (var i = 0; i < chunk.Length; i++)
            {
                var value = Math.Clamp(chunk[i], -1f, 1f);
                var sample = (short)MathF.Round(value * 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(_scratch.AsSpan(i * 2), sample);
            }

            _stream.Write(_scratch, 0, count * 2);
            offset += count;
        }

        _frames += interleaved.Length / _channels;
    }

    /// <summary>Finalizes the header sizes and releases the stream.</summary>
    public void Dispose() => Close();

    /// <summary>Finalizes the header sizes (can only be called once).</summary>
    public void Close()
    {
        if (_finalized)
            return;
        _finalized = true;

        if (!_stream.CanSeek)
        {
            _stream.Dispose();
            return;
        }

        var dataSize = _frames * _channels * 2;
        WriteHeader(dataSize);
        _stream.Dispose();
    }

    private void WriteHeader(long dataSize)
    {
        var header = new byte[44];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataSize));
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);            // fmt size
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);             // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)_channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)_sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(_sampleRate * _channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(_channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);            // bits
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataSize);

        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(header, 0, header.Length);
        _stream.Seek(0, SeekOrigin.End);
    }
}
