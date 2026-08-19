namespace Crowbar.Engine.Audio;

/// <summary>
/// A bank of one-shot SFX variants. Calling <see cref="AudioSystem.Play(SoundCue, AudioBusName, Action)"/>
/// picks a random clip and perturbs volume/pitch/pan by the configured spreads,
/// so repeated footsteps, impacts or menu clicks never sound identical.
/// </summary>
public sealed class SoundCue
{
    private readonly List<AudioClip> _clips = [];
    private readonly Random _random = new();

    public string Name { get; }

    /// <summary>Base volume before the random spread is applied.</summary>
    public float Volume = 1f;

    /// <summary>Uniform random spread added to <see cref="Volume"/> (e.g. 0.2 = +/- 20%).</summary>
    public float VolumeRandom;

    /// <summary>Base pitch before the random spread is applied.</summary>
    public float Pitch = 1f;

    /// <summary>Uniform random spread added to <see cref="Pitch"/> (e.g. 0.1 = +/- 10%).</summary>
    public float PitchRandom;

    /// <summary>Base pan before the random spread is applied.</summary>
    public float Pan;

    /// <summary>Uniform random spread added to <see cref="Pan"/> (always clamped to [-1, 1]).</summary>
    public float PanRandom;

    public bool Loop;
    public int Priority;

    /// <summary>Number of variants currently in the cue.</summary>
    public int VariantCount => _clips.Count;

    public SoundCue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Adds a variant clip to the cue.</summary>
    public SoundCue AddClip(AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clips.Add(clip);
        return this;
    }

    /// <summary>Adds several variant clips at once.</summary>
    public SoundCue AddClips(IEnumerable<AudioClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        foreach (var clip in clips)
            AddClip(clip);
        return this;
    }

    /// <summary>Picks a variant clip uniformly at random.</summary>
    public AudioClip Pick() => _clips[_random.Next(_clips.Count)];

    /// <summary>Picks a variant and rolls randomized volume/pitch/pan.</summary>
    internal (AudioClip Clip, float Volume, float Pitch, float Pan) Roll()
    {
        if (_clips.Count == 0)
            throw new InvalidOperationException($"The cue '{Name}' has no variant clips.");

        var clip = Pick();
        var volume = Volume + RandomSpread(_random, VolumeRandom);
        var pitch = Pitch + RandomSpread(_random, PitchRandom);
        var pan = Math.Clamp(Pan + RandomSpread(_random, PanRandom), -1f, 1f);
        return (clip, volume, pitch, pan);
    }

    private static float RandomSpread(Random random, float spread)
    {
        if (spread <= 0f)
            return 0f;
        var t = random.NextDouble() * 2d - 1d; // uniform in [-1, 1).
        return (float)(spread * t);
    }
}