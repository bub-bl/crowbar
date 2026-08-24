using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the precomputed <see cref="AudioClip.Envelope"/> waveform
/// min/max buckets.
/// </summary>
public class AudioClipTests
{
    [Fact]
    public void Envelope_ShortClip_OneBucketPerFrame()
    {
        // Four mono frames (upmixed to stereo): each becomes its own bucket.
        var clip = AudioClip.Create("env", 48000, new[] { 0f, 0.5f, -0.25f, 1f }, 1);

        Assert.Equal(4, clip.EnvelopeBucketCount);

        var env = clip.Envelope;
        Assert.Equal(0f, env[0], 6);
        Assert.Equal(0f, env[1], 6);
        Assert.Equal(0.5f, env[2], 6);
        Assert.Equal(0.5f, env[3], 6);
        Assert.Equal(-0.25f, env[4], 6);
        Assert.Equal(-0.25f, env[5], 6);
        Assert.Equal(1f, env[6], 6);
        Assert.Equal(1f, env[7], 6);
    }

    [Fact]
    public void Envelope_LargeClip_FoldsIntoFixedBucketsAndSpansMinMax()
    {
        const int frames = 4096;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
            mono[i] = (float)i / frames - 0.5f; // -0.5 .. ~0.5
        mono[^1] = 1f;

        var clip = AudioClip.Create("big", 48000, mono, 1);

        Assert.Equal(AudioClip.MaxEnvelopeBuckets, clip.EnvelopeBucketCount);

        var env = clip.Envelope;
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        for (var b = 0; b < clip.EnvelopeBucketCount; b++)
        {
            globalMin = Math.Min(globalMin, env[b * 2]);
            globalMax = Math.Max(globalMax, env[b * 2 + 1]);
        }

        Assert.True(globalMin <= -0.49f);
        Assert.Equal(1f, globalMax, 4);
    }

    [Fact]
    public void Envelope_EmptyClip_HasNoBuckets()
    {
        var clip = AudioClip.Create("empty", 48000, ReadOnlySpan<float>.Empty, 1);

        Assert.Equal(0, clip.Frames);
        Assert.Equal(0, clip.EnvelopeBucketCount);
        Assert.True(clip.Envelope.IsEmpty);
    }
}
