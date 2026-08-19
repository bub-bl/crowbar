using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Tests headless des décodeurs WAV/AIFF : fixtures générées en mémoire, PCM
/// comparé après décodage. Aucun fichier ni périphérique audio.
/// </summary>
public class WavDecoderTests
{
    [Fact]
    public void Decode_Stereo16Bit_RoundTripsPcm()
    {
        var source = new float[] { 0f, 0.5f, -0.5f, 0.25f, 1f, -1f };
        var bytes = AudioTestData.BuildWav(source, channels: 2);
        using var decoder = new WavDecoder(bytes);

        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
        Assert.Equal(3, decoder.TotalFrames);

        var output = new float[6];
        Assert.Equal(3, decoder.Read(output));

        for (var i = 0; i < source.Length; i++)
            Assert.Equal(source[i], output[i], 2);
    }

    [Fact]
    public void Decode_Mono16Bit_UpmixesToStereo()
    {
        var mono = new float[] { 0f, 0.5f, -0.5f };
        var bytes = AudioTestData.BuildWav(mono, channels: 1);
        using var decoder = new WavDecoder(bytes);

        var output = new float[6];
        Assert.Equal(3, decoder.Read(output));

        // Mono dupliqué en stéréo : les deux canaux portent la même valeur.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(mono[i], output[i * 2], 2);
            Assert.Equal(output[i * 2], output[i * 2 + 1], 2);
        }
    }

    [Fact]
    public void Decode_Seek_ResumesAtPosition()
    {
        var source = AudioTestData.Tone(440f, 0.1f);
        var bytes = AudioTestData.BuildWav(source, channels: 1);
        using var decoder = new WavDecoder(bytes);

        decoder.Seek(100);
        var after = new float[2];
        decoder.Read(after);

        var fromStart = new float[202];
        decoder.Seek(0);
        decoder.Read(fromStart);

        Assert.Equal(fromStart[200], after[0], 2);
        Assert.Equal(fromStart[201], after[1], 2);
    }

    [Fact]
    public void Decode_Aiff_BigEndian_RoundTripsPcm()
    {
        var source = new float[] { 0.25f, -0.25f, 0.75f, -0.75f };
        var bytes = AudioTestData.BuildAiff(source, channels: 2);
        using var decoder = new AiffDecoder(bytes);

        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(2, decoder.TotalFrames);

        var output = new float[4];
        Assert.Equal(2, decoder.Read(output));

        for (var i = 0; i < source.Length; i++)
            Assert.Equal(source[i], output[i], 2);
    }

    [Fact]
    public void Decode_InvalidData_Throws()
    {
        Assert.Throws<InvalidDataException>(() => new WavDecoder(new byte[64]));
    }
}
