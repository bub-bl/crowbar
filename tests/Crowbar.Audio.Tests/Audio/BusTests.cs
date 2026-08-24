using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for dynamically created user buses: creation, removal,
/// string-based routing and targeted StopAll.
/// </summary>
public class BusTests
{
    private const float CenterPan = 0.7071068f; // cos(pi/4) = equal-power gain at center.

    [Fact]
    public void CreateBus_AppearsInBusNames()
    {
        using var system = new AudioSystem();
        var bus = system.CreateBus("Ambient");

        Assert.Equal("Ambient", bus.Name);
        Assert.Contains("Ambient", system.BusNames);
        Assert.Same(bus, system.GetBus("Ambient"));
        Assert.Same(bus, system.GetBus("ambient")); // case-insensitive.
    }

    [Fact]
    public void CreateBus_DuplicateName_Throws()
    {
        using var system = new AudioSystem();
        system.CreateBus("Ambient");

        Assert.Throws<ArgumentException>(() => system.CreateBus("Ambient"));
        Assert.Throws<ArgumentException>(() => system.CreateBus("Master"));
        Assert.Throws<ArgumentException>(() => system.CreateBus("sfx")); // built-in collision.
    }

    [Fact]
    public void Play_OnUserBus_RoutesIntoMaster()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.CreateBus("Ambient");

        system.Play(clip, "Ambient", volume: 0.5f);

        var buffer = RenderOneBlock(system);
        var expected = 0.5f * 0.5f * CenterPan;
        Assert.Equal(expected, buffer[0], 1e-5f);
        Assert.Equal(expected, buffer[1], 1e-5f);
    }

    [Fact]
    public void Play_OnUnknownBus_Throws()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);

        Assert.Throws<ArgumentException>(() => system.Play(clip, "DoesNotExist"));
    }

    [Fact]
    public void StopAll_TargetsOnlyItsBus()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.CreateBus("Ambient");

        system.Play(clip, "Ambient", volume: 0.5f, loop: true);
        system.Play(clip, volume: 0.5f, bus: AudioBusName.Sfx, loop: true);

        system.StopAll("Ambient");
        var buffer = RenderOneBlock(system);

        var expected = 0.5f * 0.5f * CenterPan; // only Sfx remains.
        Assert.Equal(expected, buffer[0], 1e-5f);
    }

    [Fact]
    public void RemoveBus_StopsItsSounds_AndDropsFromTree()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.CreateBus("Ambient");

        system.Play(clip, "Ambient", volume: 0.5f, loop: true);
        RenderOneBlock(system);
        Assert.Equal(1, system.ActiveSoundCount);

        Assert.True(system.RemoveBus("Ambient"));
        Assert.DoesNotContain("Ambient", system.BusNames);
        Assert.Null(system.GetBus("Ambient"));

        var buffer = RenderOneBlock(system);
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Assert.Equal(0, system.ActiveSoundCount);
    }

    [Fact]
    public void RemoveBus_BuiltIn_Throws()
    {
        using var system = new AudioSystem();
        Assert.Throws<InvalidOperationException>(() => system.RemoveBus("Sfx"));
    }

    [Fact]
    public void RemoveBus_Unknown_ReturnsFalse()
    {
        using var system = new AudioSystem();
        Assert.False(system.RemoveBus("DoesNotExist"));
    }

    [Fact]
    public void GetBus_String_ResolvesBuiltInsAndMaster()
    {
        using var system = new AudioSystem();
        Assert.Same(system.Sfx, system.GetBus("Sfx"));
        Assert.Same(system.Master, system.GetBus("Master"));
        Assert.Null(system.GetBus("Nope"));
    }

    private static float[] RenderOneBlock(AudioSystem system)
    {
        var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
        system.RenderBlock(buffer);
        return buffer;
    }
}