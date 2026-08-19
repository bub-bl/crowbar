using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>Headless tests for the limiter, chorus, flanger, distortion and bitcrusher.</summary>
public class NewEffectTests
{
    private static readonly AudioEffectContext Context = new(48000, 2, 480, 0.0);

    [Fact]
    public void Limiter_DelaysSignalAndCapsCeiling()
    {
        var effect = new Limiter();
        effect.SetParameter(0, 0.5f);   // ceiling
        effect.SetParameter(1, 0.01f);  // 480-sample lookahead
        effect.SetParameter(2, 0.05f);  // release
        effect.SetParameter(3, 0f);     // no makeup
        effect.Reset();

        var loud = Constant(0.9f);
        effect.Process(loud, Context);

        // The lookahead delay means the first block is still silence.
        Assert.All(loud, sample => Assert.Equal(0f, sample, 5));

        // Two warmup blocks flush the delay line, then the steady state caps at
        // the ceiling (0.9 * (0.5 / 0.9) = 0.5).
        effect.Process(Constant(0.9f), Context);
        effect.Process(Constant(0.9f), Context);
        for (var block = 0; block < 3; block++)
        {
            var data = Constant(0.9f);
            effect.Process(data, Context);
            foreach (var sample in data)
            {
                Assert.True(sample <= 0.51f, $"limiter output exceeded the ceiling: {sample}");
                Assert.True(sample >= 0.49f, $"limiter output fell below the target: {sample}");
            }
        }
    }

    [Fact]
    public void Chorus_FixedDelay_ProducesEchoAtDelayOffset()
    {
        var effect = new Chorus();
        effect.SetParameter(0, 1f);     // fully wet
        effect.SetParameter(1, 1f);     // rate (LFO frozen at depth 0)
        effect.SetParameter(2, 0f);     // no depth modulation
        effect.SetParameter(3, 0.002f); // 96 frames.
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        Assert.Equal(0f, buffer[0], 1e-5f);
        Assert.Equal(1f, buffer[192], 1e-4f);
        Assert.Equal(1f, buffer[193], 1e-4f);
    }

    [Fact]
    public void Flanger_AtZeroMix_PassesThrough()
    {
        var effect = new Flanger();
        effect.SetParameter(0, 0f); // fully dry
        effect.SetParameter(3, 0f); // no feedback
        effect.Reset();

        var buffer = Impulse();
        effect.Process(buffer, Context);

        Assert.Equal(1f, buffer[0], 1e-5f);
        Assert.Equal(1f, buffer[1], 1e-5f);
        Assert.All(buffer[2..], sample => Assert.Equal(0f, sample, 1e-5f));
    }

    [Fact]
    public void Distortion_AtZeroDrive_PassesThrough()
    {
        var effect = new Distortion();
        effect.SetParameter(0, 0f);
        effect.SetParameter(1, 0f); // fully dry: tanh(0) must not alter the signal.
        effect.Reset();

        var buffer = Constant(0.3f);
        effect.Process(buffer, Context);

        Assert.All(buffer, sample => Assert.Equal(0.3f, sample, 1e-6f));
    }

    [Fact]
    public void Distortion_HardDrive_SaturatesButStaysBounded()
    {
        var effect = new Distortion();
        effect.SetParameter(0, 10f);
        effect.SetParameter(1, 1f);
        effect.Reset();

        var quiet = Constant(0.3f);
        effect.Process(quiet, Context);
        Assert.Equal(MathF.Tanh(3f), quiet[0], 1e-5f); // tanh(10 * 0.3).

        var huge = Constant(5f);
        effect.Process(huge, Context);
        Assert.True(huge[0] <= 1f, "distortion must stay bounded.");
        Assert.True(huge[0] > 0.9f, "the transfer curve should saturate toward 1.");
    }

    [Fact]
    public void Bitcrusher_QuantizesToStepGrid()
    {
        var effect = new Bitcrusher();
        effect.SetParameter(0, 1f);  // no downsample
        effect.SetParameter(1, 4f);  // 4-bit -> 1/8 steps.
        effect.Reset();

        var buffer = Constant(0.3f);
        effect.Process(buffer, Context);

        Assert.Equal(2f / 8f, buffer[0], 1e-5f); // round(0.3 * 8) = 2.
    }

    [Fact]
    public void Bitcrusher_Downsample_HoldsEachFrame()
    {
        var effect = new Bitcrusher();
        effect.SetParameter(0, 4f);  // hold each frame 4x.
        effect.SetParameter(1, 16f); // 16-bit, negligible quantization.
        effect.Reset();

        var buffer = new float[8];
        buffer[0] = 0.3f; // frame 0
        buffer[1] = 0.3f;
        buffer[2] = 0.7f; // frame 1
        buffer[3] = 0.7f;
        buffer[4] = 0.7f; // frame 2
        buffer[5] = 0.7f;
        buffer[6] = 0.7f; // frame 3
        buffer[7] = 0.7f;

        effect.Process(buffer, Context);

        // Frames 0..3 all output the held first frame.
        Assert.All(buffer, sample => Assert.Equal(0.3f, sample, 1e-2f));
    }

    [Fact]
    public void NewEffects_ResolveParameterNames()
    {
        Assert.Equal(0, new Limiter().GetParameterIndex("Ceiling"));
        Assert.Equal(3, new Chorus().GetParameterIndex("delay"));
        Assert.Equal(3, new Flanger().GetParameterIndex("Feedback"));
        Assert.Equal(0, new Distortion().GetParameterIndex("Drive"));
        Assert.Equal(1, new Bitcrusher().GetParameterIndex("Bits"));
    }

    private static float[] Constant(float value)
    {
        var buffer = new float[AudioSystem.BlockSize * 2];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = value;
        return buffer;
    }

    private static float[] Impulse()
    {
        var buffer = new float[AudioSystem.BlockSize * 2];
        buffer[0] = 1f;
        buffer[1] = 1f;
        return buffer;
    }
}