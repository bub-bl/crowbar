using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Mesure que le chemin de rendu DSP n'alloue rien en régime permanent, sur le
/// modèle des tests <c>Renderer2D</c>. On chauffe d'abord le JIT, puis on rend
/// des centaines de blocs avec une voix bouclée + un effet : le compteur
/// d'allocations du thread ne doit pas bouger.
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

        // Chauffe le JIT et atteint l'état stationnaire (buffers pré-dimensionnés).
        for (var i = 0; i < 200; i++)
            system.RenderBlock(buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
            system.RenderBlock(buffer);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }
}
