namespace Crowbar.Engine.Audio;

/// <summary>
/// Source d'échantillons consommée frame par frame par une voix. Cache la
/// différence entre un clip décodé en mémoire (<see cref="ClipSource"/>) et un
/// flux décodé par morceaux (<see cref="AudioStream"/>) : la voix ne connaît
/// que cette interface et n'alloue jamais en lecture.
/// </summary>
internal interface IAudioSource
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Nombre total de frames, ou <c>-1</c> si inconnu.</summary>
    long TotalFrames { get; }

    /// <summary>Remet la lecture au début.</summary>
    void Reset();

    /// <summary>
    /// Lit la frame suivante (stéréo) et avance. Retourne <c>false</c> en fin
    /// de flux (les sorties sont alors mises à zéro).
    /// </summary>
    bool TryReadFrame(out float left, out float right);
}

/// <summary>Source sur un <see cref="AudioClip"/> entièrement en mémoire.</summary>
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
