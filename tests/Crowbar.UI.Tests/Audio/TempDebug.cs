using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

public class TempDebug
{
    [Fact]
    public void Dump()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var handle = system.Play(clip, volume: 0.5f, loop: true);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        system.RenderBlock(buffer);
        var log = $"block0={buffer[0]:F6}";
        handle.FadeTo(2f, 0f);
        for (var i = 0; i < 8; i++)
        {
            system.RenderBlock(buffer);
            log += $" b{i + 1}={buffer[0]:F6}";
        }
        throw new System.Exception(log);
    }
}