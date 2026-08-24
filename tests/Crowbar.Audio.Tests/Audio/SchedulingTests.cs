using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for lookahead scheduling (<c>PlayAt</c>) and bus crossfades.
/// </summary>
public class SchedulingTests
{
    [Fact]
    public void PlayAt_StaysSilentUntilScheduledInstant()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var handle = system.PlayAt(clip, 0.02f, loop: true); // 2 blocks at 10 ms each.
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        Assert.True(handle.IsValid, "the handle must be valid immediately.");
        Assert.Equal(0, system.ActiveSoundCount);

        system.RenderBlock(buffer); // block 1: still parked.
        Assert.All(buffer, sample => Assert.Equal(0f, sample, 6));
        Assert.Equal(0, system.ActiveSoundCount);

        system.RenderBlock(buffer); // block 2: one frame short of the mark.
        Assert.All(buffer, sample => Assert.Equal(0f, sample, 6));

        system.RenderBlock(buffer); // block 3: clock reaches 20 ms -> audible.
        Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-3f);
        Assert.Equal(1, system.ActiveSoundCount);
    }

    [Fact]
    public void PlayAt_SilenceBefore_IsZeroForMultipleBlocks()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.PlayAt(clip, 0.03f, loop: true); // 3 blocks.
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 2; i++)
        {
            system.RenderBlock(buffer);
            Assert.All(buffer, sample => Assert.Equal(0f, sample, 6));
        }
    }

    [Fact]
    public void Crossfade_OldSoundFadesOut_NewStreamFadesIn()
    {
        using var system = new AudioSystem();
        var oldClip = AudioClip.Create("dc-old", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var newStream = CreateStream(0.5f, AudioSystem.BlockSize * 64);

        system.Play(oldClip, loop: true);
        var handle = system.Crossfade(newStream, 0.02f);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        var firstBlockRms = 0f;
        var midBlockRms = 0f;
        for (var i = 0; i < 6; i++)
        {
            system.RenderBlock(buffer);
            var rms = MathF.Sqrt(buffer.Select(s => s * s).Average());
            if (i == 0)
                firstBlockRms = rms;
            if (i == 3)
                midBlockRms = rms;
        }

        // The crossfade must start audibly (the old sound is still up while the
        // new stream fades in) and never go fully silent in the middle.
        Assert.True(firstBlockRms > 0.05f, "the first block of a crossfade should be audible.");
        Assert.True(midBlockRms > 0.05f, "the crossfade should not dip to silence halfway through.");
        Assert.True(handle.IsValid);
    }

    [Fact]
    public void Crossfade_StopsAllSoundsOnTheBus()
    {
        using var system = new AudioSystem();
        var oldClip = AudioClip.Create("dc-old", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var newStream = CreateStream(0.5f, AudioSystem.BlockSize * 128);

        system.Play(oldClip, bus: AudioBusName.Music, loop: true);
        system.Crossfade(newStream, 0.005f);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 32; i++)
            system.RenderBlock(buffer);

        // The old sound must have faded out and released its slot: exactly one
        // sound (the new stream) remains.
        Assert.Equal(1, system.ActiveSoundCount);
    }

    private static AudioStream CreateStream(float amplitude, int frames)
    {
        var source = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            source[i * 2] = amplitude;
            source[i * 2 + 1] = amplitude;
        }
        return new AudioStream(new WavDecoder(AudioTestData.BuildWav(source, channels: 2)));
    }
}