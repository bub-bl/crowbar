using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the threaded <see cref="AudioStream"/>: data integrity,
/// internal looping, rewind and end-of-stream. No file or audio device.
/// </summary>
public class StreamTests
{
    private const float CenterPan = 0.7071068f; // cos(pi/4) = equal-power gain at center.
    private const int FileFrames = 1000;

    /// <summary>
    /// A recognizable ramp: each sample carries its frame index, so ordering,
    /// wrapping and rewinding failures are obvious from the values.
    /// </summary>
    private static float[] BuildSource(int frames = FileFrames)
    {
        var source = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)i / frames - 0.5f;
            source[i * 2] = value;
            source[i * 2 + 1] = -value;
        }

        return source;
    }

    private static AudioStream CreateStream(float[] source, bool loop = false)
    {
        var bytes = AudioTestData.BuildWav(source, channels: 2);
        return new AudioStream(new WavDecoder(bytes), loop);
    }

    [Fact]
    public void Read_ReturnsDecodedFramesInOrder()
    {
        var source = BuildSource();
        using var stream = CreateStream(source);

        for (var i = 0; i < FileFrames; i++)
        {
            Assert.True(stream.TryReadFrame(out var left, out var right), $"Frame {i} should be available.");
            AssertClose(source[i * 2], left);
            AssertClose(source[i * 2 + 1], right);
        }

        // The producer thread needs an instant to observe end of stream and
        // publish it; TryReadFrame itself also spin-waits for that flag.
        Assert.False(stream.TryReadFrame(out _, out _));
    }

    [Fact]
    public void Loop_WrapsBackToStart()
    {
        var source = BuildSource();
        using var stream = CreateStream(source, loop: true);

        // First pass plus part of the second pass, to observe the wrap.
        var total = FileFrames + 128;
        for (var i = 0; i < total; i++)
        {
            Assert.True(stream.TryReadFrame(out var left, out var right), $"Frame {i} should be available.");
            var index = i % FileFrames;
            AssertClose(source[index * 2], left);
            AssertClose(source[index * 2 + 1], right);

            if (i % 500 == 0)
                Thread.Sleep(1);
        }
    }

    [Fact]
    public void Reset_RewindsToStart()
    {
        var source = BuildSource();
        using var stream = CreateStream(source);

        for (var i = 0; i < 400; i++)
            Assert.True(stream.TryReadFrame(out _, out _));

        stream.Reset();

        Assert.True(stream.TryReadFrame(out var left, out var right));
        AssertClose(source[0], left);
        AssertClose(source[1], right);
    }

    [Fact]
    public void Play_ThroughVoice_RendersPrimedFirstBlock()
    {
        // The first block is guaranteed primed synchronously, so it must match
        // the source deterministically and prove the stream feeds the mixer
        // headlessly (equal-power center pan is the only scaling).
        const int frames = 2000;
        var source = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = MathF.Sin(2f * MathF.PI * 440f * i / 48000f) * 0.5f;
            source[i * 2] = sample;
            source[i * 2 + 1] = sample;
        }

        var bytes = AudioTestData.BuildWav(source, channels: 2);
        using var stream = new AudioStream(new WavDecoder(bytes));
        using var system = new AudioSystem();

        var handle = system.Play(stream);
        var output = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        system.RenderBlock(output);

        for (var i = 0; i < AudioSystem.BlockSize; i++)
        {
            AssertClose(source[i * 2] * CenterPan, output[i * 2]);
            AssertClose(source[i * 2 + 1] * CenterPan, output[i * 2 + 1]);
        }

        Assert.True(handle.IsValid);
    }

    private static void AssertClose(float expected, float actual)
    {
        // 16-bit WAV quantization introduces up to ~1.5e-5 of error; a 1e-3
        // absolute tolerance is comfortably above that and well below the ramp.
        Assert.Equal((double)expected, (double)actual, 1e-3);
    }
}
