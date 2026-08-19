using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>Headless tests for the <see cref="SoundCue"/> SFX bank.</summary>
public class SoundCueTests
{
    [Fact]
    public void Pick_ReturnsOnlyAddedVariants()
    {
        var cue = new SoundCue("footstep");
        var a = AudioClip.Create("a", 48000, AudioTestData.Constant(0.1f, 8), 1);
        var b = AudioClip.Create("b", 48000, AudioTestData.Constant(0.2f, 8), 1);
        cue.AddClip(a).AddClip(b);

        var picks = Enumerable.Range(0, 64).Select(_ => cue.Pick()).ToArray();
        Assert.All(picks, clip => Assert.True(ReferenceEquals(clip, a) || ReferenceEquals(clip, b)));
    }

    [Fact]
    public void Roll_WithZeroSpread_ReturnsBaseValues()
    {
        var cue = new SoundCue("fixed");
        cue.AddClip(AudioClip.Create("a", 48000, AudioTestData.Constant(0.1f, 8), 1));

        for (var i = 0; i < 16; i++)
        {
            var (_, volume, pitch, pan) = cue.Roll();
            Assert.Equal(1f, volume, 6);
            Assert.Equal(1f, pitch, 6);
            Assert.Equal(0f, pan, 6);
        }
    }

    [Fact]
    public void Roll_WithSpreads_StaysWithinBounds()
    {
        var cue = new SoundCue("random");
        cue.AddClip(AudioClip.Create("a", 48000, AudioTestData.Constant(0.1f, 8), 1));
        cue.Volume = 0.5f;
        cue.VolumeRandom = 0.2f;
        cue.Pitch = 1f;
        cue.PitchRandom = 0.15f;
        cue.Pan = 0f;
        cue.PanRandom = 0.5f;

        for (var i = 0; i < 256; i++)
        {
            var (_, volume, pitch, pan) = cue.Roll();
            Assert.InRange(volume, 0.3f, 0.7f);
            Assert.InRange(pitch, 0.85f, 1.15f);
            Assert.InRange(pan, -1f, 1f);
        }
    }

    [Fact]
    public void Roll_EmptyCue_Throws()
    {
        var cue = new SoundCue("empty");
        Assert.Throws<InvalidOperationException>(() => cue.Roll());
    }

    [Fact]
    public void Play_Cue_ProducesAudibleOutput()
    {
        using var system = new AudioSystem();
        var cue = new SoundCue("hit");
        cue.AddClip(AudioClip.Create("hit", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize * 8), 1));

        var handle = system.Play(cue);
        Assert.True(handle.IsValid);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        system.RenderBlock(buffer);

        Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-3f);
    }

    [Fact]
    public void Play3D_Cue_Spatializes()
    {
        using var system = new AudioSystem();
        var cue = new SoundCue("steps");
        cue.AddClip(AudioClip.Create("step", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize * 8), 1));

        var handle = system.Play3D(cue, new System.Numerics.Vector3(0f, 0f, 0f));
        Assert.True(handle.IsValid);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        system.RenderBlock(buffer);

        Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-3f);
    }
}