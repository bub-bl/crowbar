using System.Buffers.Binary;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Builds in-memory audio fixtures (WAV/AIFF, tones) for headless tests: no
/// device dependency, everything is deterministic.
/// </summary>
internal static class AudioTestData
{
    /// <summary>Builds an in-memory 16-bit PCM stereo WAV.</summary>
    public static byte[] BuildWav(ReadOnlySpan<float> interleaved, int channels, int sampleRate = 48000)
    {
        var dataSize = interleaved.Length * 2;
        var bytes = new byte[44 + dataSize];

        WriteAscii(bytes, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(36 + dataSize));
        WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        WriteAscii(bytes, 36, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)dataSize);

        for (var i = 0; i < interleaved.Length; i++)
        {
            var sample = (short)(Math.Clamp(interleaved[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), sample);
        }

        return bytes;
    }

    /// <summary>Builds an in-memory 16-bit PCM stereo AIFF (big-endian).</summary>
    public static byte[] BuildAiff(ReadOnlySpan<float> interleaved, int channels, int sampleRate = 48000)
    {
        var frames = interleaved.Length / channels;
        var dataSize = interleaved.Length * 2;
        var bytes = new byte[12 + 8 + 26 + 8 + dataSize];

        WriteAscii(bytes, 0, "FORM");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(4 + 8 + 26 + 8 + dataSize));
        WriteAscii(bytes, 8, "AIFF");

        var offset = 12;
        WriteAscii(bytes, offset, "COMM");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 18);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 8), (ushort)channels);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 10), (uint)frames);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 14), 16); // bits
        WriteExtended80(bytes.AsSpan(offset + 16), sampleRate);
        offset += 8 + 18;

        WriteAscii(bytes, offset, "SSND");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), (uint)(8 + dataSize));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 0); // offset
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), 0); // blockSize
        offset += 16;

        for (var i = 0; i < interleaved.Length; i++)
        {
            var sample = (short)(Math.Clamp(interleaved[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + i * 2), sample);
        }

        return bytes;
    }

    /// <summary>Generates a mono sine wave of <paramref name="seconds"/> seconds.</summary>
    public static float[] Tone(float frequency, float seconds, int sampleRate = 48000, float amplitude = 0.5f)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
            samples[i] = MathF.Sin(2f * MathF.PI * frequency * i / sampleRate) * amplitude;
        return samples;
    }

    /// <summary>Builds a constant clip (handy for the mixer golden tests).</summary>
    public static float[] Constant(float value, int frames) => Enumerable.Repeat(value, frames).ToArray();

    /// <summary>
    /// Builds a minimal but valid MPEG-1 Layer III bitstream in memory (based on
    /// the NLayer test vector, MIT): each frame is 44.1 kHz, 128 kbps, stereo
    /// with zeroed side info, so every frame decodes to 1152 samples of silence
    /// per channel.
    /// </summary>
    public static byte[] BuildSilentMp3(int frameCount = 4)
    {
        if (frameCount < 1)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        // MPEG-1 Layer III, 44.1 kHz, 128 kbps, stereo, no CRC, no padding.
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        const int frameLength = 417; // 144 * 128000 / 44100, integer truncation.

        var data = new byte[frameLength * frameCount];
        for (var i = 0; i < frameCount; i++)
            Array.Copy(header, 0, data, i * frameLength, header.Length);
        return data;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
            buffer[offset + i] = (byte)text[i];
    }

    /// <summary>Writes an integer as an 80-bit extended float (AIFF rate).</summary>
    private static void WriteExtended80(Span<byte> destination, int value)
    {
        if (value <= 0)
            return;

        var bits = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);
        var exponent = 16383 + bits - 1;
        var mantissa = (ulong)value << (64 - bits);

        destination[0] = (byte)(exponent >> 8);
        destination[1] = (byte)exponent;
        BinaryPrimitives.WriteUInt64BigEndian(destination[2..], mantissa);
    }
}
