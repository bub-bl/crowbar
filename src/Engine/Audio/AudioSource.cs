namespace Crowbar.Engine.Audio;

/// <summary>
/// Sample source consumed frame by frame by a voice. Hides the difference
/// between an in-memory decoded clip (<see cref="ClipSource"/>) and a stream
/// decoded in chunks (<see cref="AudioStream"/>): the voice only knows this
/// interface and never allocates while reading.
/// </summary>
internal interface IAudioSource
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Total number of frames, or <c>-1</c> if unknown.</summary>
    long TotalFrames { get; }

    /// <summary>Rewinds playback to the start.</summary>
    void Reset();

    /// <summary>
    /// Reads the next frame (stereo) and advances. Returns <c>false</c> at end of
    /// stream (outputs are then zeroed).
    /// </summary>
    bool TryReadFrame(out float left, out float right);
}

/// <summary>Source over a fully in-memory <see cref="AudioClip"/>.</summary>
internal sealed class ClipSource : IAudioSource
{
    private readonly float[] _data;
    private readonly int _frames;
    private int _cursor;

    public int SampleRate { get; }
    public int Channels => 2;
    public long TotalFrames => _frames;

    public ClipSource(AudioClip clip)
    {
        SampleRate = clip.SampleRate;
        _data = clip.Data;
        _frames = clip.Frames;
    }

    public void Reset() => _cursor = 0;

    public bool TryReadFrame(out float left, out float right)
    {
        if (_cursor >= _frames)
        {
            left = 0f;
            right = 0f;
            return false;
        }

        var index = _cursor * 2;
        left = _data[index];
        right = _data[index + 1];
        _cursor++;
        return true;
    }
}
