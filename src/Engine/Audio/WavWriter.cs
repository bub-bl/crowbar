using System.Buffers.Binary;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Écrivain WAV maison (PCM 16 bits, stéréo) : il écrit un en-tête provisoire,
/// puis les échantillons au fil de l'eau, et finalise les tailles à la
/// fermeture. Le stream sous-jacent est fourni par l'appelant (fichier projet,
/// mémoire, réseau) ; la conversion float → int16 se fait dans un buffer
/// réutilisé, sans allocation en régime permanent.
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
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Seul le PCM 16 bits est écrit pour l'instant.");
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _sampleRate = sampleRate;
        _channels = channels;
        _scratch = new byte[AudioSystem.BlockSize * 2 * 2];

        WriteHeader(0);
    }

    /// <summary>Nombre de frames écrites jusqu'ici.</summary>
    public long Frames => _frames;

    /// <summary>
    /// Écrit un bloc d'échantillons <see cref="float"/> entrelacés, convertis en
    /// PCM 16 bits signé.
    /// </summary>
    public void WriteInterleaved(ReadOnlySpan<float> interleaved)
    {
        if (_finalized)
            throw new InvalidOperationException("Le WAV est déjà finalisé.");

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

    /// <summary>Finalise les tailles de l'en-tête et libère le flux.</summary>
    public void Dispose() => Close();

    /// <summary>Finalise les tailles de l'en-tête (peut être appelé une seule fois).</summary>
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
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);            // taille fmt
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
