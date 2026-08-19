using Crowbar.Engine.Audio;
using Crowbar.FileSystems;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the compressed decoders (Ogg/Vorbis via NVorbis, MP3 via
/// NLayer) and for content-based format detection in
/// <see cref="AudioDecoderFactory"/>. The OGG fixture is a small MIT test tone
/// from the NVorbis repository; the MP3 fixture is generated in memory as valid
/// silent frames.
/// </summary>
public class CompressedDecoderTests
{
    [Fact]
    public void VorbisDecoder_DecodesOggFixture_MonoToStereo()
    {
        var bytes = FileSystem.Content.ReadAllBytes("Audio/Fixtures/1test.ogg");
        using var decoder = AudioDecoderFactory.CreateFromBytes("1test.ogg", bytes);

        Assert.IsType<VorbisDecoder>(decoder);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
        Assert.Equal(17318, decoder.TotalFrames);

        var frames = (int)decoder.TotalFrames;
        var output = new float[frames * 2];
        Assert.Equal(frames, decoder.Read(output));

        // The source is mono: both output channels carry the same value.
        var peak = 0f;
        for (var i = 0; i < frames; i++)
        {
            Assert.Equal(output[i * 2], output[i * 2 + 1], 1e-6f);
            peak = MathF.Max(peak, MathF.Abs(output[i * 2]));
        }

        Assert.True(peak > 0.1f);
    }

    [Fact]
    public void VorbisDecoder_Seek_IsSampleAccurate()
    {
        var bytes = FileSystem.Content.ReadAllBytes("Audio/Fixtures/1test.ogg");
        using var decoder = AudioDecoderFactory.CreateFromBytes("1test.ogg", bytes);

        decoder.Seek(5000);
        Assert.Equal(5000, decoder.Position);

        var window = new float[480 * 2];
        Assert.Equal(480, decoder.Read(window));
        Assert.Equal(5480, decoder.Position);
    }

    [Fact]
    public void Mp3Decoder_DecodesSilentFrames_ToSilence()
    {
        var bytes = AudioTestData.BuildSilentMp3(frameCount: 4);
        using var decoder = AudioDecoderFactory.CreateFromBytes("silent.mp3", bytes);

        Assert.IsType<Mp3Decoder>(decoder);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
        Assert.Equal(4 * 1152, decoder.TotalFrames);

        var frames = (int)decoder.TotalFrames;
        var output = new float[frames * 2];
        Assert.Equal(frames, decoder.Read(output));
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Mp3Decoder_Seek_LandsOnFrameBoundary()
    {
        var bytes = AudioTestData.BuildSilentMp3(frameCount: 4);
        using var decoder = AudioDecoderFactory.CreateFromBytes("silent.mp3", bytes);

        decoder.Seek(2304); // two 1152-sample frames.
        Assert.Equal(2304, decoder.Position);

        var window = new float[480 * 2];
        Assert.Equal(480, decoder.Read(window));
        Assert.Equal(2784, decoder.Position);
    }

    [Fact]
    public void Factory_DetectsByContent_NotExtension()
    {
        var mono = AudioTestData.Tone(440f, 0.05f);
        var wav = AudioTestData.BuildWav(mono, channels: 1);

        using var decoder = AudioDecoderFactory.CreateFromBytes("renamed.ogg", wav);

        Assert.IsType<WavDecoder>(decoder);
        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(mono.Length, decoder.TotalFrames);
    }

    [Fact]
    public void Factory_OggAndMp3Magic_RouteToTheirDecoders()
    {
        var ogg = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0 };
        var oggError = Assert.ThrowsAny<Exception>(() => AudioDecoderFactory.CreateFromBytes("x.ogg", ogg));
        Assert.DoesNotContain("Unsupported audio format", oggError.Message);

        var mp3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0 };
        var mp3Error = Assert.ThrowsAny<Exception>(() => AudioDecoderFactory.CreateFromBytes("x.mp3", mp3));
        Assert.DoesNotContain("Unsupported audio format", mp3Error.Message);
    }

    [Fact]
    public void Factory_Flac_IsDeferred()
    {
        var magic = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0, 0, 0, 0 };
        var byContent = Assert.Throws<NotSupportedException>(() => AudioDecoderFactory.CreateFromBytes("x.flac", magic));
        Assert.Contains("FLAC", byContent.Message);

        var byExtension = Assert.Throws<NotSupportedException>(() => AudioDecoderFactory.CreateFromBytes("x.flac", new byte[16]));
        Assert.Contains("FLAC", byExtension.Message);
    }

    [Fact]
    public void Factory_UnknownContent_ThrowsUnsupported()
    {
        var error = Assert.Throws<NotSupportedException>(() => AudioDecoderFactory.CreateFromBytes("x.bin", new byte[16]));
        Assert.Contains("Unsupported audio format", error.Message);
    }
}
