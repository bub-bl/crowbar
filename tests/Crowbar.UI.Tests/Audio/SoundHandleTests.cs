using System.Numerics;
using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the SoundHandle live controls: pause/resume, playback
/// position and duration, fade above unity, Doppler and bus-gain smoothing.
/// </summary>
public class SoundHandleTests
{
    private const float CenterPan = 0.7071068f;

    [Fact]
    public void Pause_FreezesPosition_ResumeAdvances()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("tone", 48000, AudioTestData.Tone(440f, 0.5f), 1);
        var handle = system.Play(clip);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 10; i++)
            system.RenderBlock(buffer);
        var before = handle.PositionSeconds;
        Assert.True(before > 0f);

        handle.Pause();
        system.RenderBlock(buffer);
        Assert.True(handle.IsPaused);
        var frozen = handle.PositionSeconds;
        Assert.True(MathF.Abs(frozen - before) < 0.001f);

        system.RenderBlock(buffer);
        Assert.Equal(frozen, handle.PositionSeconds, 3);

        handle.Resume();
        system.RenderBlock(buffer);
        Assert.False(handle.IsPaused);
        Assert.True(handle.PositionSeconds > frozen);
    }

    [Fact]
    public void PausedSound_KeepsProducingFrozenFrame()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var handle = system.Play(clip, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);
        handle.Pause();
        system.RenderBlock(buffer);

        Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-3f);
    }

    [Fact]
    public void DurationSeconds_MatchesClip()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("tone", 48000, AudioTestData.Tone(440f, 0.25f), 1);
        var handle = system.Play(clip);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);
        Assert.Equal(0.25f, handle.DurationSeconds, 2);
    }

    [Fact]
    public void FadeTo_CanRiseAboveUnity()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var handle = system.Play(clip, volume: 0.5f, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer); // settles at 0.5 * 0.5 * CenterPan.
        handle.FadeTo(2f, 0f);
        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer); // gain converges toward 1.0.

        var expected = 0.5f * 1f * CenterPan; // 2x the initial 0.5 gain.
        Assert.Equal(expected, buffer[0], 1e-4f);
    }

    [Fact]
    public void FadeToStop_SilencesAndReleasesSlot()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var handle = system.Play(clip, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);
        handle.FadeToStop(0.02f);
        for (var i = 0; i < 16; i++)
            system.RenderBlock(buffer);

        Assert.All(buffer, sample => Assert.Equal(0f, sample, 4));
        Assert.Equal(0, system.ActiveSoundCount);
        Assert.False(handle.IsValid);
    }

    [Fact]
    public void Doppler_RaisesPitchWhenListenerCloses()
    {
        var clip = AudioClip.Create("tone", 48000, AudioTestData.Tone(1000f, 1f), 1);

        var reference = RenderDoppler(clip, listenerVelocity: Vector3.Zero);
        var moving = RenderDoppler(clip, listenerVelocity: new Vector3(0f, 0f, -34.3f)); // ~1.1x toward the source.

        var refCrossings = CountZeroCrossings(reference);
        var movingCrossings = CountZeroCrossings(moving);

        Assert.True(movingCrossings > refCrossings * 1.05f,
            $"Doppler expected more zero crossings, got {movingCrossings} vs {refCrossings}.");
    }

    [Fact]
    public void BusGain_IsSmoothedAcrossTheBlock()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.Play(clip, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer); // steady state at gain 1.

        system.Sfx.Gain = 2f;
        system.RenderBlock(buffer);

        var old = 0.5f * CenterPan;
        var doubled = 0.5f * 2f * CenterPan;
        Assert.Equal(old, buffer[0], 1e-2f);        // block starts at the old gain...
        Assert.Equal(doubled, buffer[^1], 1e-2f);   // ...and ramps to the new one.
    }

    private static float[] RenderDoppler(AudioClip clip, Vector3 listenerVelocity)
    {
        using var system = new AudioSystem();
        system.Listener.Position = new Vector3(0f, 0f, 10f); // away from the source.
        system.Listener.Rolloff = 0f; // keep full amplitude.
        system.Listener.AirAbsorption = 0f;
        system.Listener.Velocity = listenerVelocity;

        system.Play3D(clip, new Vector3(0f, 0f, 0f), loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        for (var i = 0; i < 64; i++)
            system.RenderBlock(buffer);
        return buffer;
    }

    private static int CountZeroCrossings(float[] samples)
    {
        var count = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0f && samples[i] >= 0f) || (samples[i - 1] >= 0f && samples[i] < 0f))
                count++;
        }
        return count;
    }
}