using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for side-chain ducking and auxiliary (reverb-style) sends.
/// </summary>
public class DuckingAndSendTests
{
    private const float CenterPan = 0.7071068f;

    [Fact]
    public void Duck_LowersTargetGain_AndReleases()
    {
        using var system = new AudioSystem();
        var musicClip = AudioClip.Create("music", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        var sfxClip = AudioClip.Create("sfx", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize * 64), 1);

        system.Play(musicClip, bus: AudioBusName.Music, loop: true);
        system.Sfx.Duck(system.Music, amount: 0.25f, attackSeconds: 0.01f, releaseSeconds: 0.01f);

        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer); // music alone at unity.

        var musicAlone = buffer[0];
        Assert.Equal(0.5f * CenterPan, musicAlone, 1e-3f);

        var sfxHandle = system.Play(sfxClip, bus: AudioBusName.Sfx);
        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer); // duck kicks in.

        // Master = ducked music (0.5 x 0.25 x CenterPan) + the ducking SFX itself.
        Assert.Equal(0.5f * 0.25f * CenterPan + 0.5f * CenterPan, buffer[0], 1e-3f);

        sfxHandle.Stop();
        for (var i = 0; i < 80; i++)
            system.RenderBlock(buffer); // duck releases once the peak meter decays.

        Assert.Equal(0.5f * CenterPan, buffer[0], 1e-3f);
    }

    [Fact]
    public void AddSend_RoutesPostEffectsOutputToTarget()
    {
        using var system = new AudioSystem();
        var reverb = system.CreateBus("Reverb");
        var clip = AudioClip.Create("dry", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize * 8), 1);

        system.Sfx.AddSend(reverb!, 0.5f);
        system.Play(clip);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer);

        var dryPeak = system.Sfx.Peak;
        var wetPeak = reverb!.Peak;
        Assert.True(dryPeak > 1e-3f, "the dry bus must be audible.");
        Assert.Equal(0.5f, wetPeak / dryPeak, 1e-2f);
    }

    [Fact]
    public void AddSend_SelfAndCycle_AreRejected()
    {
        using var system = new AudioSystem();
        var a = system.CreateBus("A")!;
        var b = system.CreateBus("B")!;
        var reverb = system.CreateBus("Reverb")!;

        Assert.Throws<ArgumentException>(() => a.AddSend(a, 0.5f));

        reverb.AddSend(b, 0.5f);
        Assert.Throws<ArgumentException>(() => b.AddSend(reverb, 0.5f));
    }

    [Fact]
    public void SendChain_ProcessesWetThroughTargetEffects()
    {
        using var system = new AudioSystem();
        var reverb = system.CreateBus("Reverb")!;
        var gain = new GainEffect();
        gain.SetParameter(0, 2f);
        reverb.AddEffect(gain);

        var clip = AudioClip.Create("dry", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize * 8), 1);
        system.Sfx.AddSend(reverb, 0.5f);
        system.Play(clip);
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];

        for (var i = 0; i < 8; i++)
            system.RenderBlock(buffer);

        // Reverb's own gain doubles the received wet signal.
        Assert.Equal(2f * 0.5f, reverb.Peak / system.Sfx.Peak, 1e-2f);
    }
}