using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// Une voix du pool : elle lit une <see cref="IAudioSource"/> (clip ou stream),
/// ré-échantillonne à la volée (pitch par interpolation linéaire), applique
/// volume/fade/pan/atténuation 3D puis sa chaîne d'effets, et somme le bloc
/// dans le bus cible. Tout l'état de rendu vit uniquement sur le thread DSP ;
/// le thread de jeu ne fait que réserver le slot et envoyer des commandes.
/// </summary>
internal sealed class AudioVoice
{
    private const int MaxEffects = 8;

    /// <summary>État cross-thread du slot (Free/Reserved/Active).</summary>
    public volatile int State;

    /// <summary>Génération du slot, incrémentée à chaque réservation.</summary>
    public volatile int Generation;

    // --- État de contrôle (écrit par le DSP lors du traitement des commandes) ---
    public IAudioSource? Source;
    public float Volume = 1f;
    public float Pitch = 1f;
    public float Pan;
    public bool Loop;
    public int Priority;
    public bool Spatial;
    public Vector3 Position;
    public float FadeInSeconds;

    // --- Chaîne d'effets (possédée par le DSP) ---
    private readonly IAudioEffect?[] _effects = new IAudioEffect?[MaxEffects];
    private int _effectCount;

    // --- État de rendu (DSP uniquement) ---
    private readonly float[] _scratch = new float[AudioSystem.BlockSize * AudioSystem.Channels];
    private double _readPosition;
    private int _nextIndex;
    private float _curL, _curR, _nextL, _nextR;
    private bool _ended;
    private float _fadeGain = 1f;
    private float _fadeFrom = 1f;
    private float _fadeTarget = 1f;
    private float _fadeRemaining;
    private float _fadeDuration;
    private float _currentGain = 1f;
    private float _currentPanL = 0.7071068f;
    private float _currentPanR = 0.7071068f;
    private float _distanceGain = 1f;
    private float _spatialPan;

    public AudioBus? Target { get; set; }

    public bool HasEnded => _ended;

    public void ResetRender()
    {
        _readPosition = 0;
        _ended = false;
        _fadeGain = FadeInSeconds > 0f ? 0f : 1f;
        _fadeFrom = _fadeGain;
        _fadeTarget = 1f;
        _fadeRemaining = FadeInSeconds;
        _fadeDuration = FadeInSeconds;
        _distanceGain = 1f;
        _spatialPan = 0;

        // Snap des gains/pan au réglage courant : une voix neuve démarre à son
        // volume cible, sans artefact de lissage depuis une valeur arbitraire.
        var initialPan = Math.Clamp(Pan, -1f, 1f);
        var initialAngle = (initialPan + 1f) * (MathF.PI / 4f);
        _currentPanL = MathF.Cos(initialAngle);
        _currentPanR = MathF.Sin(initialAngle);
        _currentGain = Math.Clamp(Volume * _fadeGain, 0f, 16f);

        LoadHead();
        for (var i = 0; i < _effectCount; i++)
            _effects[i]!.Reset();
    }

    public void ClearEffects() => _effectCount = 0;

    public void AddEffect(IAudioEffect effect)
    {
        if (_effectCount >= MaxEffects)
            return;
        _effects[_effectCount++] = effect;
        effect.Reset();
    }

    public void SetEffectParameter(int effectIndex, int parameterIndex, float value)
    {
        if (effectIndex >= 0 && effectIndex < _effectCount)
            _effects[effectIndex]!.SetParameter(parameterIndex, value);
    }

    public void FadeTo(float target, float duration)
    {
        _fadeTarget = Math.Clamp(target, 0f, 1f);
        _fadeFrom = _fadeGain;
        _fadeDuration = Math.Max(0f, duration);
        _fadeRemaining = _fadeDuration;
        if (_fadeDuration <= 0f)
            _fadeGain = _fadeTarget;
    }

    public void Render(AudioListener listener, int frames, double time)
    {
        var target = Target;
        if (target is null || Source is null || State != (int)VoiceState.Active)
            return;

        UpdateSpatial(listener);

        // Gain et pan par bloc, lissés sur quelques millisecondes.
        var gainTarget = Math.Clamp(Volume * _fadeGain * _distanceGain, 0f, 16f);
        var effectivePan = Math.Clamp(Pan + _spatialPan, -1f, 1f);
        var angle = (effectivePan + 1f) * (MathF.PI / 4f);
        var panL = MathF.Cos(angle);
        var panR = MathF.Sin(angle);

        var smoothing = 1f - MathF.Pow(0.0001f, 1f / (AudioSystem.SampleRate * 0.005f));
        _currentGain += (gainTarget - _currentGain) * smoothing;
        _currentPanL += (panL - _currentPanL) * smoothing;
        _currentPanR += (panR - _currentPanR) * smoothing;

        var audible = _currentGain > 0.0005f && target.Gain > 0.0005f;
        var scratch = _scratch;
        var pitch = Pitch > 0f ? Pitch : 0f;

        if (!audible)
        {
            // Voix virtuelle : on avance la tête de lecture sans produire de son
            // ni toucher aux buffers (l'état est conservé pour la reprise).
            AdvanceSilent(frames, pitch);
            Array.Clear(scratch);
        }
        else
        {
            FillScratch(scratch, frames, pitch);
            ApplyEffects(scratch, frames, time);

            var acc = target.Accumulator;
            for (var i = 0; i < frames * 2; i++)
                acc[i] += scratch[i];
        }

        UpdateFade(frames);
    }

    private void FillScratch(float[] scratch, int frames, float pitch)
    {
        if (Source is null)
        {
            Array.Clear(scratch);
            _ended = true;
            return;
        }

        if (pitch == 0f)
        {
            // Moteur arrêté : sortie figée sur la frame courante (pas de lecture).
            for (var i = 0; i < frames; i++)
            {
                scratch[i * 2] = _curL * _currentGain * _currentPanL;
                scratch[i * 2 + 1] = _curR * _currentGain * _currentPanR;
            }
            return;
        }

        for (var i = 0; i < frames; i++)
        {
            if (_ended)
            {
                scratch[i * 2] = 0f;
                scratch[i * 2 + 1] = 0f;
                continue;
            }

            // Avance la source jusqu'à encadrer la position fractionnaire. Le
            // plancher est recalculé à chaque itération : un bouclage (loop)
            // ré-écrit _readPosition, il ne faut donc jamais réutiliser un
            // plancher périmé.
            while (_nextIndex <= (int)_readPosition)
                AdvanceFrame();

            // Quand AdvanceFrame vient de détecter la fin de flux, _cur porte
            // encore la dernière frame valide : on l'émet une dernière fois ici
            // (la prochaine itération verra _ended et produira du silence).
            var floor = (int)_readPosition;
            var fraction = (float)(_readPosition - floor);
            var left = _curL + (_nextL - _curL) * fraction;
            var right = _curR + (_nextR - _curR) * fraction;

            scratch[i * 2] = left * _currentGain * _currentPanL;
            scratch[i * 2 + 1] = right * _currentGain * _currentPanR;

            _readPosition += pitch;
        }
    }

    private void AdvanceSilent(int frames, float pitch)
    {
        // La source n'est pas consommée ici : quand la voix redevient audible,
        // la boucle `while (_nextIndex <= floor)` de FillScratch rattrape la
        // source vers l'avant (le flux ne se lit que vers l'avant). Pour un
        // clip bouclé, on borne la position et on réaligne la tête pour éviter
        // un rattrapage O(temps de silence).
        _readPosition += pitch * frames;
        if (Loop && Source is not null && Source.TotalFrames > 0)
        {
            _readPosition %= Source.TotalFrames;
            Source.Reset();
            LoadHead();
        }
    }

    private void AdvanceFrame()
    {
        if (Source is null)
        {
            _ended = true;
            return;
        }

        _curL = _nextL;
        _curR = _nextR;
        _nextIndex++;

        if (Source.TryReadFrame(out var left, out var right))
        {
            _nextL = left;
            _nextR = right;
            return;
        }

        // Fin de flux.
        if (Loop)
        {
            // _nextIndex vaut ici le nombre de frames consommées dans ce
            // passage : on rebobine _readPosition d'autant (la partie
            // fractionnaire est conservée) avant de relire la tête du flux.
            var consumed = _nextIndex;
            Source.Reset();
            _readPosition -= consumed;
            LoadHead();
        }
        else
        {
            _nextL = _curL;
            _nextR = _curR;
            _ended = true;
        }
    }

    private void LoadHead()
    {
        _nextIndex = 0;
        if (Source is null)
        {
            _ended = true;
            return;
        }

        if (!Source.TryReadFrame(out _curL, out _curR))
        {
            _ended = true;
            return;
        }
        _nextIndex = 1;

        if (!Source.TryReadFrame(out _nextL, out _nextR))
        {
            _nextL = _curL;
            _nextR = _curR;
        }
    }

    private void ApplyEffects(float[] scratch, int frames, double time)
    {
        if (_effectCount == 0)
            return;

        var context = new AudioEffectContext(AudioSystem.SampleRate, AudioSystem.Channels, frames, time);
        for (var i = 0; i < _effectCount; i++)
            _effects[i]!.Process(scratch, context);
    }

    private void UpdateFade(int frames)
    {
        if (_fadeRemaining <= 0f)
        {
            _fadeGain = _fadeTarget;
            return;
        }

        _fadeRemaining -= frames / (float)AudioSystem.SampleRate;
        if (_fadeRemaining <= 0f)
        {
            _fadeGain = _fadeTarget;
            _fadeRemaining = 0f;
            return;
        }

        var progress = 1f - _fadeRemaining / Math.Max(_fadeDuration, 1e-6f);
        _fadeGain = _fadeFrom + (_fadeTarget - _fadeFrom) * progress;
    }

    private void UpdateSpatial(AudioListener listener)
    {
        if (!Spatial)
        {
            _distanceGain = 1f;
            _spatialPan = 0f;
            return;
        }

        var delta = Position - listener.Position;
        var distance = delta.Length();
        var safe = Math.Max(0.01f, distance);

        // Atténuation inverse-distance bornée : 1 / (1 + d * rolloff).
        _distanceGain = Math.Clamp(1f / (1f + safe * listener.Rolloff), 0f, 1f);

        // Panoramique latéral dans l'espace du listener.
        var normalized = delta / safe;
        var lateral = Vector3.Dot(normalized, listener.Right);
        _spatialPan = Math.Clamp(lateral, -1f, 1f);
    }
}
