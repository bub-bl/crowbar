using System.Runtime.InteropServices;
using Silk.NET.SDL;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Backend audio SDL2 en mode poussée : ouvre un périphérique de sortie avec
/// <c>SDL_OpenAudioDevice</c> et alimente la file avec <c>SDL_QueueAudio</c>.
/// Le runtime produit du stéréo <see cref="float"/> entrelacé ; cette classe est
/// la seule à connaître SDL et convertit vers le format réellement négocié
/// (float32, s16, s32 ou u8, mono ou stéréo) dans un buffer réutilisé.
///
/// Migration SDL3 : remplacer le couple <c>SDL_OpenAudioDevice</c>/
/// <c>SDL_QueueAudio</c> par <c>SDL_OpenAudioDeviceStream</c>/
/// <c>SDL_PutAudioStreamData</c> — seule cette classe change, pas le runtime.
/// </summary>
internal sealed unsafe class SdlAudioBackend : IAudioBackend
{
    private readonly Sdl _sdl;
    private readonly uint _device;
    private readonly ushort _format;
    private readonly int _channels;
    private readonly byte[] _scratch;
    private bool _running;
    private bool _disposed;

    public string BackendName => "SDL2 (queue push)";
    public int SampleRate { get; }
    public int Channels => _channels;
    public IReadOnlyList<AudioDevice> OutputDevices { get; }
    public IReadOnlyList<AudioDevice> InputDevices { get; }
    public bool IsRunning => _running && !_disposed;
    public uint QueuedSamples => _sdl.GetQueuedAudioSize(_device) / (uint)Math.Max(1, _channels * BytesPerSample(_format));

    public SdlAudioBackend()
    {
        _sdl = Sdl.GetApi();
        if (_sdl.InitSubSystem(Sdl.InitAudio) < 0)
            throw new InvalidOperationException($"SDL audio init failed: {_sdl.GetErrorS()}");

        OutputDevices = Enumerate(_sdl, isCapture: 0);
        InputDevices = Enumerate(_sdl, isCapture: 1);

        // Le moteur est calé sur 48 kHz : on n'autorise pas SDL à changer la
        // fréquence, mais on accepte tout format/canaux et on convertit.
        var desired = new AudioSpec
        {
            Freq = AudioSystem.SampleRate,
            Format = (ushort)Sdl.AudioF32,
            Channels = 2,
            Samples = (ushort)AudioSystem.BlockSize
        };
        var obtained = new AudioSpec();
        var allowed = Sdl.AudioAllowFormatChange | Sdl.AudioAllowChannelsChange | Sdl.AudioAllowSamplesChange;

        _device = _sdl.OpenAudioDevice((string?)null, 0, in desired, ref obtained, allowed);
        if (_device == 0)
            throw new InvalidOperationException($"SDL audio device open failed: {_sdl.GetErrorS()}");

        SampleRate = obtained.Freq;
        _format = obtained.Format;
        _channels = obtained.Channels is 1 or 2 ? obtained.Channels : 2;
        _scratch = new byte[AudioSystem.BlockSize * 2 * 4];
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sdl.PauseAudioDevice(_device, 0);
        _running = true;
    }

    public void Stop()
    {
        if (_disposed || _device == 0)
            return;
        _sdl.PauseAudioDevice(_device, 1);
        _running = false;
    }

    public bool QueueSamples(ReadOnlySpan<float> interleaved)
    {
        if (!_running)
            return false;

        var frames = interleaved.Length / 2;
        var byteLength = Convert(interleaved, frames);

        fixed (byte* pointer = _scratch)
            return _sdl.QueueAudio(_device, pointer, (uint)byteLength) == 0;
    }

    public IAudioCaptureDevice? OpenCapture(string? device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SdlCaptureDevice.Open(_sdl, device);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_device != 0)
        {
            _sdl.CloseAudioDevice(_device);
        }
        _sdl.QuitSubSystem(Sdl.InitAudio);
        _sdl.Dispose();
    }

    /// <summary>Convertit un bloc stéréo float vers le format/canaux négociés, dans le scratch.</summary>
    private int Convert(ReadOnlySpan<float> interleaved, int frames)
    {
        var bytesPerSample = BytesPerSample(_format);
        var mono = _channels == 1;

        switch (_format)
        {
            case (ushort)Sdl.AudioF32:
            {
                var dst = MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, frames * _channels * bytesPerSample));
                for (var i = 0; i < frames; i++)
                {
                    var left = interleaved[i * 2];
                    if (mono)
                        dst[i] = (left + interleaved[i * 2 + 1]) * 0.5f;
                    else
                    {
                        dst[i * 2] = left;
                        dst[i * 2 + 1] = interleaved[i * 2 + 1];
                    }
                }
                return frames * _channels * bytesPerSample;
            }
            case (ushort)Sdl.AudioS16:
            {
                var dst = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, frames * _channels * bytesPerSample));
                for (var i = 0; i < frames; i++)
                {
                    var left = Clamp16(interleaved[i * 2]);
                    if (mono)
                        dst[i] = Clamp16((interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f);
                    else
                    {
                        dst[i * 2] = left;
                        dst[i * 2 + 1] = Clamp16(interleaved[i * 2 + 1]);
                    }
                }
                return frames * _channels * bytesPerSample;
            }
            case (ushort)Sdl.AudioS32:
            {
                var dst = MemoryMarshal.Cast<byte, int>(_scratch.AsSpan(0, frames * _channels * bytesPerSample));
                for (var i = 0; i < frames; i++)
                {
                    var left = Clamp32(interleaved[i * 2]);
                    if (mono)
                        dst[i] = Clamp32((interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f);
                    else
                    {
                        dst[i * 2] = left;
                        dst[i * 2 + 1] = Clamp32(interleaved[i * 2 + 1]);
                    }
                }
                return frames * _channels * bytesPerSample;
            }
            default:
            {
                for (var i = 0; i < frames; i++)
                {
                    var sample = mono
                        ? (interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f
                        : interleaved[i * 2];
                    _scratch[i] = (byte)(Math.Clamp(sample * 0.5f + 0.5f, 0f, 1f) * 255f);
                }
                return frames;
            }
        }
    }

    private static short Clamp16(float value) => (short)Math.Clamp((int)(value * 32767f), -32768, 32767);

    private static int Clamp32(float value) => (int)Math.Clamp(value * 2147483647f, -2147483648f, 2147483647f);

    private static int BytesPerSample(ushort format) => format switch
    {
        (ushort)Sdl.AudioF32 => 4,
        (ushort)Sdl.AudioS32 => 4,
        (ushort)Sdl.AudioS16 => 2,
        _ => 1
    };

    private static IReadOnlyList<AudioDevice> Enumerate(Sdl sdl, int isCapture)
    {
        var devices = new List<AudioDevice>();
        var count = sdl.GetNumAudioDevices(isCapture);
        for (var i = 0; i < count; i++)
        {
            var name = sdl.GetAudioDeviceNameS(i, isCapture);
            devices.Add(new AudioDevice(name ?? $"Device {i}", i == 0));
        }
        return devices;
    }

    /// <summary>Périphérique de capture SDL2 : <c>SDL_DequeueAudio</c> en mono/stéréo float.</summary>
    private sealed class SdlCaptureDevice : IAudioCaptureDevice
    {
        private readonly Sdl _sdl;
        private readonly uint _device;
        private readonly ushort _format;
        private readonly int _sourceChannels;
        private readonly byte[] _scratch;
        private bool _disposed;

        public int SampleRate { get; }
        public int Channels => 2;

        private SdlCaptureDevice(Sdl sdl, uint device, ushort format, int sourceChannels, int sampleRate)
        {
            _sdl = sdl;
            _device = device;
            _format = format;
            _sourceChannels = sourceChannels;
            SampleRate = sampleRate;
            _scratch = new byte[AudioSystem.BlockSize * 2 * 4];
        }

        public static SdlCaptureDevice? Open(Sdl sdl, string? device)
        {
            var desired = new AudioSpec
            {
                Freq = AudioSystem.SampleRate,
                Format = (ushort)Sdl.AudioF32,
                Channels = 1,
                Samples = 1024
            };
            var obtained = new AudioSpec();
            var allowed = (int)Sdl.AudioAllowAnyChange;
            var id = sdl.OpenAudioDevice(device!, 1, in desired, ref obtained, allowed);
            if (id == 0)
                return null;

            return new SdlCaptureDevice(sdl, id, obtained.Format, obtained.Channels, obtained.Freq);
        }

        public void Start() => _sdl.PauseAudioDevice(_device, 0);

        public int Read(Span<float> destination)
        {
            if (_disposed)
                return 0;

            var maxFrames = destination.Length / 2;
            var bytesPerSample = BytesPerSample(_format);
            var available = _sdl.GetQueuedAudioSize(_device);
            if (available == 0)
                return 0;

            var byteLength = (int)Math.Min(available, _scratch.Length);
            fixed (byte* pointer = _scratch)
            {
                var got = _sdl.DequeueAudio(_device, pointer, (uint)byteLength);
                byteLength = (int)got;
            }

            var frames = byteLength / (_sourceChannels * bytesPerSample);
            frames = Math.Min(frames, maxFrames);

            for (var i = 0; i < frames; i++)
            {
                var left = ReadSample(_scratch, i, 0, bytesPerSample);
                var right = _sourceChannels > 1
                    ? ReadSample(_scratch, i, 1, bytesPerSample)
                    : left;
                destination[i * 2] = left;
                destination[i * 2 + 1] = right;
            }

            return frames;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _sdl.CloseAudioDevice(_device);
        }

        private float ReadSample(byte[] buffer, int frame, int channel, int bytesPerSample)
        {
            var offset = (frame * _sourceChannels + channel) * bytesPerSample;
            return _format switch
            {
                (ushort)Sdl.AudioF32 => BitConverter.ToSingle(buffer, offset),
                (ushort)Sdl.AudioS16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                (ushort)Sdl.AudioS32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => (buffer[offset] - 128) / 128f
            };
        }
    }
}
