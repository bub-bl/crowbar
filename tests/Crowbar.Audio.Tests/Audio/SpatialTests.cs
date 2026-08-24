using System.Numerics;
using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for 3D spatialization (inverse-distance attenuation and air
/// absorption). No device, fully deterministic offline rendering.
/// </summary>
public class SpatialTests
{
    [Fact]
    public void Play3D_AirAbsorption_AttenuatesHighFrequencies()
    {
        const int frames = AudioSystem.BlockSize;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
            mono[i] = MathF.Sin(2f * MathF.PI * 10000f * i / 48000f) * 0.5f;

        var clip = AudioClip.Create("10k", 48000, mono, 1);
        var position = new Vector3(0f, 0f, 10f);

        var reference = RenderSteadyState(clip, position, absorption: 0f);
        var filtered = RenderSteadyState(clip, position, absorption: 1f);

        // The 10 kHz tone survives with no absorption and is rolled off hard
        // once absorption is enabled at the same distance.
        Assert.True(Rms(filtered) < Rms(reference) * 0.5f);
    }

    [Fact]
    public void Play3D_DistanceAttenuation_ReducesGain()
    {
        var clip = AudioClip.Create(
            "dc",
            48000,
            AudioTestData.Constant(0.5f, AudioSystem.BlockSize),
            1);

        var near = RenderSteadyState(clip, new Vector3(0f, 0f, 0.1f), absorption: 0f);
        var far = RenderSteadyState(clip, new Vector3(0f, 0f, 100f), absorption: 0f);

        Assert.True(Rms(far) < Rms(near) * 0.5f);
    }

    private static float[] RenderSteadyState(AudioClip clip, Vector3 position, float absorption)
    {
        using var system = new AudioSystem();
        system.Listener.Rolloff = 0.1f; // keep the distance gain moderate.
        system.Listener.AirAbsorption = absorption;

        system.Play3D(clip, position, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        // Skip the per-sound gain-smoothing transient, then measure the
        // settled block.
        for (var i = 0; i < 64; i++)
            system.RenderBlock(buffer);

        return buffer;
    }

    private static double Rms(float[] samples)
    {
        var sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
            sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / samples.Length);
    }
}
