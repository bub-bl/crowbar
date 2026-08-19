using Crowbar.Engine.Audio;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the microphone buffer: push known frames (or let a fake
/// device fill the ring buffer) then verify streamed reading and the replayable
/// snapshot. No real device is required.
/// </summary>
public class MicrophoneTests
{
    [Fact]
    public void Push_ReadConsumesOldestFirst()
    {
        using var system = new AudioSystem();
        var microphone = system.Microphone;

        microphone.Push(new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f });

        var destination = new float[6];
        Assert.Equal(3, microphone.Read(destination));
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f }, destination);
        Assert.Equal(0, microphone.AvailableFrames);
    }

    [Fact]
    public void Push_TakeClip_SnapshotsMostRecentWithoutConsuming()
    {
        using var system = new AudioSystem();
        var microphone = system.Microphone;

        microphone.Push(new float[] { 1f, 1f, 2f, 2f, 3f, 3f });

        var clip = microphone.TakeClip();
        Assert.Equal(3, clip.Frames);
        Assert.Equal(1f, clip.Data[0]);
        Assert.Equal(3f, clip.Data[4]);

        // Non-destructive: streamed reading still finds the 3 frames.
        var destination = new float[6];
        Assert.Equal(3, microphone.Read(destination));
        Assert.Equal(1f, destination[0]);
        Assert.Equal(3f, destination[4]);
    }

    [Fact]
    public void Start_WithFakeDevice_CapturesIntoBuffer()
    {
        using var backend = new FakeAudioBackend(new FakeCaptureDevice());
        using var system = new AudioSystem(backend);

        Assert.True(system.Microphone.Start(bufferSeconds: 1f));

        try
        {
            Assert.True(System.Threading.SpinWait.SpinUntil(() => system.Microphone.AvailableFrames > 0, 2000));
            var clip = system.Microphone.TakeClip();
            Assert.True(clip.Frames > 0);
            Assert.Equal(0.5f, clip.Data[0], 1e-6f);
            Assert.Equal(0.25f, clip.Data[1], 1e-6f);
        }
        finally
        {
            system.Microphone.Stop();
        }

        Assert.False(system.Microphone.IsActive);
    }

    [Fact]
    public void Start_WithoutBackend_ReturnsFalse()
    {
        using var system = new AudioSystem();
        Assert.False(system.Microphone.Start());
        Assert.False(system.Microphone.IsActive);
    }

    [Fact]
    public void Push_UpdatesLevelMeters()
    {
        using var system = new AudioSystem();
        var microphone = system.Microphone;

        var block = new[] { 0.5f, 0.5f, 0.5f, 0.5f };
        for (var i = 0; i < 6; i++)
            microphone.Push(block); // converge the smoothed rms toward 0.5.

        Assert.Equal(0.5f, microphone.Peak, 1e-4f);
        Assert.Equal(0.5f, microphone.Rms, 1e-3f);
    }

    private sealed class FakeAudioBackend(IAudioCaptureDevice? capture) : IAudioBackend
    {
        public string BackendName => "Fake";
        public int SampleRate => 48000;
        public int Channels => 2;
        public IReadOnlyList<AudioDevice> OutputDevices => [];
        public IReadOnlyList<AudioDevice> InputDevices => [new AudioDevice("FakeMic", true)];
        public bool IsRunning => true;
        public uint QueuedSamples => 0;

        public bool QueueSamples(ReadOnlySpan<float> interleaved) => true;
        public void Start() { }
        public void Stop() { }
        public void RefreshDevices() { }
        public IAudioCaptureDevice? OpenCapture(string? device) => capture;
        public void Dispose() { }
    }

    private sealed class FakeCaptureDevice : IAudioCaptureDevice
    {
        public int SampleRate => 48000;
        public int Channels => 2;

        public void Start() { }

        public int Read(Span<float> destination)
        {
            var frames = Math.Min(destination.Length / 2, 480);
            for (var i = 0; i < frames; i++)
            {
                destination[i * 2] = 0.5f;
                destination[i * 2 + 1] = 0.25f;
            }
            return frames;
        }

        public void Dispose() { }
    }
}
