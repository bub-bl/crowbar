using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Verifies that the DSP render path allocates nothing in steady state, on the
/// model of the <c>Renderer2D</c> tests. The JIT is warmed up first, then
/// hundreds of blocks are rendered with a looped voice + an effect: the thread's
/// allocation counter must not move.
/// </summary>
public class ZeroAllocationTests
{
    [Fact]
    public void RenderBlock_SteadyState_DoesNotAllocate()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("tone", 48000, AudioTestData.Tone(440f, 1f), 1);
        var handle = system.Play(clip, volume: 0.5f, loop: true);
        handle.AddEffect(new SchroederReverb());

        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        // Warm up the JIT and reach steady state (pre-sized buffers).
        for (var i = 0; i < 200; i++)
            system.RenderBlock(buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
            system.RenderBlock(buffer);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }
}
