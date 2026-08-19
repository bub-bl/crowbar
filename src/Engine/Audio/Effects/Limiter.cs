namespace Crowbar.Engine.Audio;

/// <summary>
/// Feed-forward lookahead limiter: the signal is delayed by <c>Lookahead</c>
/// seconds while a sliding-window peak detector finds the loudest upcoming
/// sample, so the gain reduction kicks in *before* the peak reaches the output.
/// A per-channel smoothed envelope prevents pumping.
///
/// Parameters: <c>0</c> = ceiling (0..1), <c>1</c> = lookahead (s),
/// <c>2</c> = release (s), <c>3</c> = makeup gain (dB).
/// </summary>
public sealed class Limiter : IAudioEffect
{
    private volatile float _ceiling = 0.95f;
    private volatile float _lookahead = 0.005f;
    private volatile float _release = 0.05f;
    private volatile float _makeupDb = 0f;

    private float[] _delay = [];
    private int _head;
    private int _window;
    private readonly float[] _gain = [1f, 1f];

    public int ParameterCount => 4;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var frames = context.Frames;
        var length = frames * 2;
        var window = Math.Max(0, (int)(Math.Max(0f, _lookahead) * context.SampleRate));
        var capacity = (window + 2) * 2;
        if (_delay.Length < capacity)
        {
            _delay = new float[capacity];
            _head = 0;
            _gain[0] = _gain[1] = 1f;
        }
        _window = window;

        var ceiling = Math.Clamp(_ceiling, 0.05f, 1f);
        var makeup = MathF.Pow(10f, Math.Clamp(_makeupDb, 0f, 24f) / 20f);
        var releaseCoef = MathF.Exp(-1f / (Math.Max(0.0001f, _release) * context.SampleRate));
        var attackCoef = MathF.Exp(-1f / (Math.Max(0.0001f, 0.0002f) * context.SampleRate));

        for (var i = 0; i < length; i += 2)
        {
            // Push the incoming frame into the ring buffer.
            var idx = _head;
            _delay[idx] = buffer[i];
            _delay[idx + 1] = buffer[i + 1];
            _head = (idx + 2) % _delay.Length;

            // Peak of the lookahead window (the upcoming N frames, including this one).
            var peak = 0f;
            var read = idx;
            for (var w = 0; w <= window; w++)
            {
                var m = MathF.Abs(_delay[read]);
                if (m > peak)
                    peak = m;
                var m2 = MathF.Abs(_delay[read + 1]);
                if (m2 > peak)
                    peak = m2;
                read -= 2;
                if (read < 0)
                    read = _delay.Length - 2;
            }

            var target = ceiling / Math.Max(peak, 1e-6f);
            if (target > 1f)
                target = 1f;

            // The output is the sample pushed `window + 1` frames ago, i.e. the
            // oldest entry in the ring (one past the head).
            var outIdx = (idx + 2) % _delay.Length;

            for (var channel = 0; channel < 2; channel++)
            {
                var gain = target < _gain[channel] ? attackCoef : releaseCoef;
                _gain[channel] = _gain[channel] * gain + target * (1f - gain);
                buffer[i + channel] = _delay[outIdx + channel] * _gain[channel] * makeup;
            }
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _ceiling,
        1 => _lookahead,
        2 => _release,
        3 => _makeupDb,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _ceiling = Math.Clamp(value, 0.05f, 1f);
                break;
            case 1:
                _lookahead = Math.Clamp(value, 0f, 0.05f);
                break;
            case 2:
                _release = Math.Clamp(value, 0.0001f, 5f);
                break;
            case 3:
                _makeupDb = Math.Clamp(value, 0f, 24f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public int GetParameterIndex(string name)
    {
        if (EffectParameterNames.Equals(name, "Ceiling"))
            return 0;
        if (EffectParameterNames.Equals(name, "Lookahead"))
            return 1;
        if (EffectParameterNames.Equals(name, "Release"))
            return 2;
        if (EffectParameterNames.Equals(name, "Makeup"))
            return 3;
        return -1;
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _head = 0;
        _gain[0] = _gain[1] = 1f;
    }
}