namespace Crowbar.Engine.Audio;

/// <summary>An enumerated audio device (playback or capture).</summary>
public readonly record struct AudioDevice(string Name, bool IsDefault);

/// <summary>
/// Audio capture stream (microphone) opened by an <see cref="IAudioBackend"/>:
/// it provides interleaved stereo <see cref="float"/> frames (mono duplicated by
/// the backend) via <see cref="Read"/>, into a caller-reused buffer.
/// </summary>
public interface IAudioCaptureDevice : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Drains up to <c>destination.Length / 2</c> captured frames; returns the
    /// number of frames read (0 if none is available yet).
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Starts capture (once the device has been opened).</summary>
    void Start();
}

/// <summary>
/// Audio backend: the single layer that talks to the platform. The current
/// implementation (<see cref="SdlAudioBackend"/>) uses the SDL2 push API
/// (<c>SDL_OpenAudioDevice</c> + <c>SDL_QueueAudio</c>). Migrating to SDL3
/// (<c>SDL_PutAudioStreamData</c> + streams) will only touch this class, not the
/// runtime (<see cref="AudioSystem"/>) nor the effects.
///
/// The runtime pushes interleaved stereo <see cref="float"/> blocks and never
/// touches the native buffers.
/// </summary>
public interface IAudioBackend : IDisposable
{
    string BackendName { get; }

    /// <summary>Effective sample rate of the output device.</summary>
    int SampleRate { get; }

    /// <summary>Effective channel count of the output (the runtime produces stereo).</summary>
    int Channels { get; }

    /// <summary>Available playback devices.</summary>
    IReadOnlyList<AudioDevice> OutputDevices { get; }

    /// <summary>Available capture devices.</summary>
    IReadOnlyList<AudioDevice> InputDevices { get; }

    /// <summary>Re-enumerates the available playback and capture devices (hot-plug).</summary>
    void RefreshDevices();

    /// <summary>True as long as the backend can accept blocks.</summary>
    bool IsRunning { get; }

    /// <summary>Number of samples still queued in the device (latency).</summary>
    uint QueuedSamples { get; }

    /// <summary>Pushes an interleaved stereo block. Returns false on failure.</summary>
    bool QueueSamples(ReadOnlySpan<float> interleaved);

    /// <summary>Starts playback (takes the device out of pause).</summary>
    void Start();

    /// <summary>Pauses playback.</summary>
    void Stop();

    /// <summary>Opens a capture device (microphone), or null if unavailable.</summary>
    IAudioCaptureDevice? OpenCapture(string? device);
}
