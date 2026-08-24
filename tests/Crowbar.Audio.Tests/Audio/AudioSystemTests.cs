using Crowbar.Engine.Audio;
using Crowbar.FileSystems;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Deterministic offline rendering of the mixer: no backend, the memory buffer
/// is compared to expected values. These tests cover the bus tree, sounds,
/// volume, mute and the clock.
/// </summary>
public class AudioSystemTests
{
    private const float CenterPan = 0.7071068f; // cos(pi/4) = equal-power gain at center.

    [Fact]
    public void RenderBlock_WithNoSounds_OutputsSilence()
    {
        using var system = new AudioSystem();
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Assert.Equal(AudioSystem.BlockSize, system.Clock.PositionSamples);
    }

    [Fact]
    public void Play_ConstantClip_RendersEqualPowerCenter()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        system.Play(clip);
        var buffer = RenderOneBlock(system);

        var expected = 0.5f * CenterPan;
        for (var i = 0; i < buffer.Length; i++)
            Assert.Equal(expected, buffer[i], 1e-5f);
    }

    [Fact]
    public void Play_RespectsVolume()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        system.Play(clip, volume: 0.25f);
        var buffer = RenderOneBlock(system);

        var expected = 0.5f * 0.25f * CenterPan;
        Assert.Equal(expected, buffer[0], 1e-5f);
        Assert.Equal(expected, buffer[1], 1e-5f);
    }

    [Fact]
    public void Play_RoutesToBusAndMuteCutsBus()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        system.Play(clip, bus: AudioBusName.Music);
        Assert.NotEqual(0f, RenderOneBlock(system)[0]);

        system.Music.Mute = true;
        var muted = RenderOneBlock(system);
        Assert.All(muted, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Golden_BusSummation_AddsLeafBusesIntoMaster()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.25f, AudioSystem.BlockSize), 1);

        system.Play(clip, bus: AudioBusName.Sfx);
        system.Play(clip, bus: AudioBusName.Music);

        var buffer = RenderOneBlock(system);
        var expected = 0.5f * CenterPan; // 0.25 + 0.25, center pan.

        Assert.Equal(expected, buffer[0], 1e-5f);
        Assert.Equal(expected, buffer[1], 1e-5f);
    }

    [Fact]
    public void Stop_ReleasesSoundAndSilences()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        var handle = system.Play(clip, loop: true);
        Assert.NotEqual(0f, RenderOneBlock(system)[0]);
        Assert.Equal(1, system.ActiveSoundCount);

        handle.Stop();
        var buffer = RenderOneBlock(system);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Assert.Equal(0, system.ActiveSoundCount);
    }

    [Fact]
    public void RenderBlock_AdvancesClockPerBlock()
    {
        using var system = new AudioSystem();
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);
        system.RenderBlock(buffer);
        system.RenderBlock(buffer);

        Assert.Equal(AudioSystem.BlockSize * 3L, system.Clock.PositionSamples);
    }

    [Fact]
    public void Play_ByPath_LoadsAndPlaysClip()
    {
        using var system = new AudioSystem();
        var path = $"test-tone-{Guid.NewGuid():N}.wav";
        try
        {
            // The wav lives in the project's Content/ folder and is addressed
            // explicitly — content paths are always explicit.
            var mono = AudioTestData.Tone(440f, 0.1f);
            FileSystem.Project.WriteAllBytes($"Content/{path}", AudioTestData.BuildWav(mono, 1));

            system.Play($"Content/{path}");
            var buffer = RenderOneBlock(system);

            Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-4f);
        }
        finally
        {
            var contentPath = $"Content/{path}";
            AudioClip.Invalidate(contentPath);
            if (FileSystem.Project.FileExists(contentPath))
                FileSystem.Project.DeleteFile(contentPath);
        }
    }

    [Fact]
    public void Play_SoloCutsNonSoloBuses()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        system.Play(clip, bus: AudioBusName.Sfx);
        system.Play(clip, bus: AudioBusName.Music);
        system.Music.Solo = true;

        var buffer = RenderOneBlock(system);
        var expected = 0.5f * CenterPan; // only Music passes.
        Assert.Equal(expected, buffer[0], 1e-5f);
    }

    [Fact]
    public void Play_OnCompleted_FiresOnceWhenSoundEnds()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("short", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        var calls = 0;
        system.Play(clip, onCompleted: () => calls++);

        // The block consumes the whole clip, so the sound ends during render,
        // but the callback is only dispatched on the game-thread tick.
        system.RenderBlock(buffer);
        Assert.Equal(0, calls);

        system.Update(0f);
        Assert.Equal(1, calls);

        // A second tick must not re-fire the same completion.
        system.Update(0f);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Play_OnCompleted_NotFiredWhenStopped()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("loop", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        var calls = 0;
        var handle = system.Play(clip, loop: true, onCompleted: () => calls++);

        system.RenderBlock(buffer);
        handle.Stop();
        system.RenderBlock(buffer); // applies the stop and releases the sound.

        system.Update(0f);
        Assert.Equal(0, calls);
    }

    private static float[] RenderOneBlock(AudioSystem system)
    {
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        system.RenderBlock(buffer);
        return buffer;
    }
}
