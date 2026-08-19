using System.Buffers.Binary;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Hand-written AIFF (FORM/AIFF) decoder, big-endian. Handles signed 8/16/24/32
/// bit PCM (unlike WAV, 8-bit AIFF is signed). Output is normalized to
/// interleaved stereo <see cref="float"/>, like <see cref="WavDecoder"/>.
/// </summary>
public sealed class AiffDecoder : IAudioDecoder
{
    private readonly byte[] _data;
    private readonly int _dataOffset;
    private readonly int _dataLength;
    private readonly int _sourceChannels;
    private readonly int _bitsPerSample;
    private readonly int _blockAlign;
    private long _position;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames { get; }
    public long Position => _position;

    public AiffDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        if (data.Length < 12 || ReadTag(data, 0) != "FORM" || ReadTag(data, 8) != "AIFF")
            throw new InvalidDataException("This file is not a valid AIFF (FORM/AIFF).");

        var offset = 12;
        var channels = 0;
        var bits = 0;
        var sampleRate = 0;
        long totalFrames = 0;
        var foundData = false;

        while (offset + 8 <= data.Length)
        {
            var id = ReadTag(data, offset);
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
            var body = offset + 8;

            if (id == "COMM")
            {
                if (body + 18 > data.Length)
                    throw new InvalidDataException("Truncated AIFF 'COMM' chunk.");

                channels = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(body));
                totalFrames = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 2));
                bits = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(body + 6));
                sampleRate = (int)Math.Round(ReadExtended80(body + 8));
            }
            else if (id == "SSND")
            {
                // offset (4) + blockSize (4), then the audio data.
                var dataStart = body + 8;
                _dataOffset = dataStart;
                _dataLength = (int)Math.Min(size - 8, data.Length - dataStart);
                foundData = true;
            }

            offset = body + (int)size + (int)(size % 2);
        }

        if (!foundData || _dataLength <= 0)
            throw new InvalidDataException("The AIFF contains no 'SSND' chunk.");

        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        _sourceChannels = channels > 0 ? channels : 1;
        _bitsPerSample = bits;
        _blockAlign = Math.Max(1, (_sourceChannels * Math.Max(1, bits / 8)));
        TotalFrames = totalFrames > 0 ? totalFrames : _dataLength / _blockAlign;
    }

    public int Read(Span<float> destination)
    {
        var frames = destination.Length / 2;
        var bytesPerSample = Math.Max(1, _bitsPerSample / 8);
        var produced = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            var dataIndex = _dataOffset + (int)_position * _blockAlign;
            if (dataIndex + _blockAlign > _dataOffset + _dataLength)
                break;

            var left = 0f;
            var right = 0f;

            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                var sample = ReadSample(dataIndex + channel * bytesPerSample);
                if (channel == 0)
                    left = sample;
                else if (channel == 1)
                    right = sample;
            }

            if (_sourceChannels == 1)
                right = left;

            destination[frame * 2] = left;
            destination[frame * 2 + 1] = right;
            _position++;
            produced++;
        }

        return produced;
    }

    public void Seek(long frame)
    {
        _position = Math.Clamp(frame, 0, TotalFrames);
    }

    public void Dispose()
    {
    }

    private float ReadSample(int offset) => _bitsPerSample switch
    {
        8 => (sbyte)_data[offset] / 128f,
        16 => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset)) / 32768f,
        24 => ReadInt24BigEndian(offset) / 8388608f,
        32 => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(offset)) / 2147483648f,
        _ => (sbyte)_data[offset] / 128f
    };

    private int ReadInt24BigEndian(int offset)
    {
        var b0 = _data[offset];
        var b1 = _data[offset + 1];
        var b2 = _data[offset + 2];
        var value = (b0 << 16) | (b1 << 8) | b2;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    /// <summary>
    /// Reads an 80-bit IEEE 754 extended float (the AIFF sample-rate format) and
    /// converts it to <see cref="double"/>.
    /// </summary>
    private double ReadExtended80(int offset)
    {
        var sign = (_data[offset] & 0x80) != 0 ? -1 : 1;
        var exponent = ((_data[offset] & 0x7F) << 8) | _data[offset + 1];
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(offset + 2));

        if (exponent == 0 && mantissa == 0)
            return 0;

        // The mantissa has its explicit integer bit at the head: it is a fixed
        // point with 63 fractional bits, shifted by (exponent - bias).
        return sign * (double)mantissa * Math.Pow(2, exponent - 16383 - 63);
    }

    private static string ReadTag(byte[] data, int offset) =>
        $"{(char)data[offset]}{(char)data[offset + 1]}{(char)data[offset + 2]}{(char)data[offset + 3]}";
}
