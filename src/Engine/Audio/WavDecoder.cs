using System.Buffers.Binary;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Décodeur WAV (RIFF/WAVE) écrit maison. Gère les formats PCM 8/16/24/32 bits,
/// le flottant 32 bits et l'en-tête extensible (<c>WAVE_FORMAT_EXTENSIBLE</c>).
/// Le contenu est lu depuis un <c>byte[]</c> chargé une fois, et la sortie est
/// toujours normalisée en stéréo <see cref="float"/> entrelacé (mono dupliqué,
/// les canaux surnuméraires sont ignorés).
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    private readonly byte[] _data;
    private readonly int _dataOffset;
    private readonly int _dataLength;
    private readonly int _sourceChannels;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;
    private readonly bool _isUnsigned;
    private readonly int _blockAlign;
    private long _position;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames { get; }
    public long Position => _position;

    public WavDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        if (data.Length < 12 || ReadTag(data, 0) != "RIFF" || ReadTag(data, 8) != "WAVE")
            throw new InvalidDataException("Ce fichier n'est pas un WAV (RIFF/WAVE) valide.");

        var offset = 12;
        short format = 0;
        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var foundData = false;

        while (offset + 8 <= data.Length)
        {
            var id = ReadTag(data, offset);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            var body = offset + 8;

            if (id == "fmt ")
            {
                if (body + 16 > data.Length)
                    throw new InvalidDataException("En-tête WAV 'fmt ' tronqué.");

                format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));

                // WAVE_FORMAT_EXTENSIBLE : le vrai format est le début du GUID
                // de sous-format, placé après les champs extensibles.
                if (format == unchecked((short)0xFFFE) && body + 40 <= data.Length)
                    format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                _dataOffset = body;
                _dataLength = (int)Math.Min(size, data.Length - body);
                foundData = true;
            }

            offset = body + (int)size + (int)(size % 2);
        }

        if (!foundData || _dataLength <= 0)
            throw new InvalidDataException("Le WAV ne contient pas de chunk 'data'.");

        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        _sourceChannels = channels > 0 ? channels : 1;
        _bitsPerSample = bits;
        _isFloat = format == 3;
        _isUnsigned = !_isFloat && bits == 8;
        if (_blockAlign <= 0)
            _blockAlign = _sourceChannels * Math.Max(1, bits / 8);
        TotalFrames = _dataLength / _blockAlign;
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
                var sampleOffset = dataIndex + channel * bytesPerSample;
                var sample = ReadSample(sampleOffset);
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

    private float ReadSample(int offset)
    {
        if (_isFloat)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        if (_isUnsigned)
            return (_data[offset] - 128) / 128f;

        return _bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(offset)) / 32768f,
            24 => ReadInt24LittleEndian(offset) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset)) / 2147483648f,
            _ => (_data[offset] - 128) / 128f
        };
    }

    private int ReadInt24LittleEndian(int offset)
    {
        var b0 = _data[offset];
        var b1 = _data[offset + 1];
        var b2 = _data[offset + 2];
        var value = b0 | (b1 << 8) | (b2 << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static string ReadTag(byte[] data, int offset) =>
        $"{(char)data[offset]}{(char)data[offset + 1]}{(char)data[offset + 2]}{(char)data[offset + 3]}";
}
