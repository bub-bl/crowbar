using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the DSP effects: pass-through, silence, panning, impulse
/// response and compressor non-linearity. Each effect is a pure C# stereo block,
/// without a backend.
/// </summary>
public class EffectTests
{
    private static readonly AudioEffectContext Context = new(48000, 2, 480, 0.0);

    [Fact]
    public void Gain_Unit_PassesThrough()
    {
        var effect = new GainEffect();
        var buffer = Impulse();

        effect.Process(buffer, Context);

        Assert.Equal(1f, buffer[0]);
        Assert.All(buffer[2..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Gain_Zero_Silences()
    {
        var effect = new GainEffect();
        effect.SetParameter(0, 0f);
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Pan_Center_KeepsChannelsEqual()
    {
        var effect = new PanEffect();
        effect.SetParameter(0, 0f);
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        Assert.Equal(buffer[0], buffer[1], 1e-5f);
        Assert.NotEqual(0f, buffer[0]);
    }

    [Fact]
    public void Pan_HardLeft_SilencesRight()
    {
        var effect = new PanEffect();
        effect.SetParameter(0, -1f);
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        Assert.NotEqual(0f, buffer[0]);
        Assert.Equal(0f, buffer[1], 1e-5f);
    }

    [Fact]
    public void Biquad_Impulse_IsDeterministicAndDecays()
    {
        var effect = new BiquadFilter(BiquadType.LowPass, 1000f, 0.707f);
        effect.Reset();

        var first = Impulse();
        effect.Process(first, Context);

        var second = Impulse();
        effect.Reset();
        effect.Process(second, Context);

        // Deterministic after Reset.
        Assert.Equal(first, second);
        // An impulse response: non-zero first output then decay.
        Assert.NotEqual(0f, first[0]);
        Assert.True(MathF.Abs(first[2]) < 1f);
    }

    [Fact]
    public void Delay_ProducesEchoAtDelayOffset()
    {
        var effect = new DelayEffect();
        var delayFrames = 100;
        effect.SetParameter(0, delayFrames / 48000f);
        effect.SetParameter(1, 0f);   // no feedback
        effect.SetParameter(2, 1f);   // fully wet
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        // The echo of sample 0 arrives at frame 100, i.e. stereo index 200.
        Assert.Equal(1f, buffer[200], 1e-5f);
        Assert.Equal(1f, buffer[201], 1e-5f);
    }

    [Fact]
    public void Reverb_ExtendsImpulseIntoATail()
    {
        var effect = new SchroederReverb();
        effect.SetParameter(0, 0.9f); // wet
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        // The reverberated tail appears a few blocks after the impulse.
        var tailEnergy = 0f;
        for (var block = 0; block < 12; block++)
        {
            var tail = new float[AudioSystem.BlockSize * 2];
            effect.Process(tail, Context);
            foreach (var sample in tail)
                tailEnergy = MathF.Max(tailEnergy, MathF.Abs(sample));
        }

        Assert.True(tailEnergy > 1e-4f);
    }

    [Fact]
    public void Compressor_ReducesLoudPeaks()
    {
        var effect = new Compressor();
        effect.SetParameter(0, -12f);   // threshold -12 dB ~= 0.25
        effect.SetParameter(1, 8f);     // ratio
        effect.SetParameter(2, 0.001f); // fast attack
        effect.SetParameter(3, 0.05f);  // short release
        effect.SetParameter(4, 0f);     // no makeup
        effect.Reset();

        var loud = new float[AudioSystem.BlockSize * 2];
        for (var i = 0; i < loud.Length; i++)
            loud[i] = 0.9f;

        var peak = 0f;
        foreach (var sample in loud)
            peak = MathF.Max(peak, MathF.Abs(sample));
        var before = peak;

        effect.Process(loud, Context);

        var after = 0f;
        foreach (var sample in loud)
            after = MathF.Max(after, MathF.Abs(sample));

        Assert.True(after < before);
    }

    private static float[] Impulse()
    {
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        buffer[0] = 1f;
        buffer[1] = 1f;
        return buffer;
    }
}
