using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>
/// A pool voice: it reads an <see cref="IAudioSource"/> (clip or stream),
/// resamples on the fly (pitch via linear interpolation), applies
/// volume/fade/pan/3D attenuation then its effect chain, and sums the block into
/// the target bus. All render state lives only on the DSP thread; the game
/// thread only reserves the slot and sends commands.
/// </summary>
internal sealed class AudioVoice
{
    private const int MaxEffects = 8;

    /// <summary>Cross-thread state of the slot (Free/Reserved/Active).</summary>
    public volatile int State;

    /// <summary>Slot generation, incremented on every reservation.</summary>
    public volatile int Generation;

    // --- Control state (written by the DSP while processing commands) ---
    public IAudioSource? Source;
    public float Volume = 1f;
    public float Pitch = 1f;
    public float Pan;
    public bool Loop;
    public int Priority;
    public bool Spatial;
    public Vector3 Position;
    public float FadeInSeconds;

    // --- Effect chain (owned by the DSP) ---
    private readonly IAudioEffect?[] _effects = new IAudioEffect?[MaxEffects];
    private int _effectCount;

    // --- Render state (DSP only) ---
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

        // Snap gain/pan to the current setting: a fresh voice starts at its
        // target volume, without smoothing artifacts from an arbitrary value.
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

        // Per-block gain and pan, smoothed over a few milliseconds.
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
            // Virtual voice: advance the read head without producing sound or
            // touching the buffers (state is kept for resumption).
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
            // Engine stopped: output frozen on the current frame (no read).
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

            // Advance the source until it brackets the fractional position. The
            // floor is recomputed every iteration: a loop rewrites
            // _readPosition, so a stale floor must never be reused.
            while (_nextIndex <= (int)_readPosition)
                AdvanceFrame();

            // When AdvanceFrame has just detected end of stream, _cur still
            // holds the last valid frame: emit it one last time here (the next
            // iteration will see _ended and produce silence).
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
        if (Source is { LoopsInternally: true })
        {
            // A live stream cannot be skipped: advance through it at the normal
            // pitch (discarding the samples) so the read head stays aligned
            // with the source cursor. Resuming audibility then needs no catch-up.
            for (var i = 0; i < frames; i++)
            {
                while (_nextIndex <= (int)_readPosition)
                    AdvanceFrame();
                _readPosition += pitch;
            }
            return;
        }

        // In-memory clip: skip ahead without reading samples. When the voice
        // becomes audible again, the `while (_nextIndex <= floor)` loop of
        // FillScratch catches the source up forward. For a looped clip, the
        // position is bounded and the head realigned to avoid an O(silence
        // duration) catch-up.
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

        // End of stream.
        if (Loop && !Source.LoopsInternally)
        {
            // _nextIndex here is the number of frames consumed in this pass: rewind
            // _readPosition by that amount (the fractional part is kept) before
            // re-reading the head of the stream.
            var consumed = _nextIndex;
            Source.Reset();
            _readPosition -= consumed;
            LoadHead();
        }
        else
        {
            // A source that loops internally never reports end of stream, so
            // reaching this point means the decoder truly stalled (or a
            // non-looping stream ended): report the end instead of blocking the
            // DSP thread on a cross-thread rewind.
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

        // Bounded inverse-distance attenuation: 1 / (1 + d * rolloff).
        _distanceGain = Math.Clamp(1f / (1f + safe * listener.Rolloff), 0f, 1f);

        // Lateral panning in listener space.
        var normalized = delta / safe;
        var lateral = Vector3.Dot(normalized, listener.Right);
        _spatialPan = Math.Clamp(lateral, -1f, 1f);
    }
}
