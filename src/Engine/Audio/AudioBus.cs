namespace Crowbar.Engine.Audio;

/// <summary>
/// Nœud de l'arbre de mixage : le master agrège ses bus enfants (Musique, SFX,
/// UI, Voix), chaque bus somme les voix qui lui sont routées puis applique sa
/// chaîne d'effets. Gain, mute et solo sont des champs volatils lisibles/écrits
/// sans verrou depuis le thread de jeu.
///
/// La chaîne d'effets est configurée avant <see cref="AudioSystem.Start"/> (les
/// paramètres, eux, restent modifiables à chaud via
/// <see cref="SetEffectParameter"/>).
/// </summary>
public sealed class AudioBus
{
    private const int MaxEffects = 8;

    private readonly IAudioEffect?[] _effects = new IAudioEffect?[MaxEffects];
    private int _effectCount;
    private volatile float _peak;
    private volatile float _rms;

    public string Name { get; }

    /// <summary>Gain linéaire du bus (1 = unité).</summary>
    public volatile float Gain = 1f;

    /// <summary>Coupe le bus (sortie à zéro) sans détruire ses voix.</summary>
    public volatile bool Mute;

    /// <summary>Fait passer ce bus en solo (les autres bus sont coupés).</summary>
    public volatile bool Solo;

    /// <summary>Buffer d'accumulation du bloc en cours (frames × 2, stéréo).</summary>
    internal float[] Accumulator { get; }

    /// <summary>Crête du dernier bloc rendu (0..1), lissée.</summary>
    public float Peak => _peak;

    /// <summary>RMS du dernier bloc rendu (0..1), lissé.</summary>
    public float Rms => _rms;

    public int EffectCount => _effectCount;

    internal AudioBus(string name, int blockFrames)
    {
        Name = name;
        Accumulator = new float[blockFrames * 2];
    }

    /// <summary>Ajoute un effet en fin de chaîne (avant <see cref="AudioSystem.Start"/>).</summary>
    public void AddEffect(IAudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (_effectCount >= MaxEffects)
            throw new InvalidOperationException($"Un bus accepte au plus {MaxEffects} effets.");
        _effects[_effectCount++] = effect;
    }

    /// <summary>Retourne l'effet à <paramref name="index"/>, ou null.</summary>
    public IAudioEffect? GetEffect(int index) => index >= 0 && index < _effectCount ? _effects[index] : null;

    /// <summary>Écrit un paramètre d'un effet de la chaîne (atomique, sans verrou).</summary>
    public void SetEffectParameter(int effectIndex, int parameterIndex, float value) =>
        _effects[effectIndex]?.SetParameter(parameterIndex, value);

    internal void Clear() => Array.Clear(Accumulator);

    internal void ProcessEffects(int frames, double time)
    {
        if (_effectCount == 0)
            return;

        var context = new AudioEffectContext(AudioSystem.SampleRate, AudioSystem.Channels, frames, time);
        for (var i = 0; i < _effectCount; i++)
            _effects[i]!.Process(Accumulator, context);
    }

    internal void SetMeters(float peak, float rms, float smoothing)
    {
        _peak = Math.Max(peak, _peak * smoothing);
        _rms = rms * (1f - smoothing) + _rms * smoothing;
    }
}
