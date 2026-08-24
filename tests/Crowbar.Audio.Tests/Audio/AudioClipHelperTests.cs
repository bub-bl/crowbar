using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>Headless tests for the AudioClip editing helpers.</summary>
public class AudioClipHelperTests
{
    private static AudioClip Stereo(string name, float[] interleaved) =>
        AudioClip.Create(name, 48000, interleaved, 2);

    [Fact]
    public void ToMono_AveragesBothChannels()
    {
        var clip = Stereo("stereo", new[] { 0.4f, 0.6f, -0.2f, 0.4f });

        var mono = AudioClip.ToMono(clip);

        Assert.Equal(2, mono.Frames);
        Assert.Equal(0.5f, mono.Data[0], 1e-6f);
        Assert.Equal(0.5f, mono.Data[1], 1e-6f);
        Assert.Equal(0.1f, mono.Data[2], 1e-6f);
        Assert.Equal(0.1f, mono.Data[3], 1e-6f);
    }

    [Fact]
    public void Normalize_ScalesToRequestedPeak()
    {
        var clip = Stereo("quiet", new[] { 0.25f, -0.5f, 0.125f, 0f });

        var normalized = AudioClip.Normalize(clip, peak: 0.9f);

        // Max |sample| = 0.5 -> scale 1.8.
        Assert.Equal(0.45f, normalized.Data[0], 1e-5f);
        Assert.Equal(-0.9f, normalized.Data[1], 1e-5f);
        Assert.Equal(0.225f, normalized.Data[2], 1e-5f);
    }

    [Fact]
    public void Normalize_Silence_IsIdentity()
    {
        var clip = Stereo("silent", new float[4]);
        Assert.Same(clip, AudioClip.Normalize(clip));
    }

    [Fact]
    public void Resample_DoublesFrameCountWithInterpolation()
    {
        var clip = Stereo("ramp", new[] { 0f, 0f, 1f, 1f, 2f, 2f, 3f, 3f }); // 4 frames.

        var resampled = AudioClip.Resample(clip, 96000);

        Assert.Equal(8, resampled.Frames);
        Assert.Equal(96000, resampled.SampleRate);
        Assert.Equal(0f, resampled.Data[0], 1e-5f);     // frame 0
        Assert.Equal(0.5f, resampled.Data[2], 1e-5f);   // frame 1: interp(0..1)
        Assert.Equal(3f, resampled.Data[14], 1e-5f);    // last frame saturates to 3.
    }

    [Fact]
    public void Resample_SameRate_ReturnsSameInstance()
    {
        var clip = Stereo("same", new[] { 0.1f, 0.2f });
        Assert.Same(clip, AudioClip.Resample(clip, 48000));
    }

    [Fact]
    public void Concat_JoinsClipsInOrder()
    {
        var a = Stereo("a", new[] { 1f, 1f });
        var b = Stereo("b", new[] { 2f, 2f, 3f, 3f });

        var joined = AudioClip.Concat(a, b);

        Assert.Equal(3, joined.Frames);
        Assert.Equal(new float[] { 1f, 1f, 2f, 2f, 3f, 3f }, joined.Data);
    }

    [Fact]
    public void Concat_MismatchedRates_Throws()
    {
        var a = Stereo("a", new[] { 1f, 1f });
        var b = AudioClip.Create("b", 44100, new[] { 2f, 2f }, 1);
        Assert.Throws<ArgumentException>(() => AudioClip.Concat(a, b));
    }
}