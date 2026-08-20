namespace Crowbar.Engine.Audio;

/// <summary>
/// Classic modulated-delay chorus: a few LFO-driven delay taps (per channel,
/// slightly out of phase for width) are mixed with the dry signal.
///
/// Parameters: <c>0</c> = mix (0..1), <c>1</c> = rate (Hz),
/// <c>2</c> = depth (s of delay modulation), <c>3</c> = base delay (s).
/// </summary>
public sealed class Chorus : IAudioEffect
{
    private volatile float _mix = 0.35f;
    private volatile float _rate = 0.8f;
    private volatile float _depth = 0.004f;
    private volatile float _baseDelay = 0.02f;

    private float[] _delay = [];
    private int _head;
    private readonly float[] _phase = [0f, MathF.PI / 2f]; // 90 degrees apart.

    public int ParameterCount => 4;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var maxDelay = _baseDelay + _depth;
        var capacity = (int)((maxDelay * context.SampleRate) + 8) * 2;
        if (_delay.Length < capacity)
        {
            _delay = new float[capacity];
            _head = 0;
        }

        var sr = context.SampleRate;
        var mix = Math.Clamp(_mix, 0f, 1f);
        var dry = 1f - mix;
        var rate = Math.Max(0.01f, _rate);
        var depth = Math.Max(0f, _depth);
        var baseDelay = Math.Max(depth, _baseDelay);
        var phaseStep = 2f * MathF.PI * rate / sr;

        for (var i = 0; i < buffer.Length; i += 2)
        {
            var idx = _head;
            _delay[idx] = buffer[i];
            _delay[idx + 1] = buffer[i + 1];
            _head = (idx + 2) % _delay.Length;

            for (var channel = 0; channel < 2; channel++)
            {
                _phase[channel] += phaseStep;
                if (_phase[channel] > 2f * MathF.PI)
                    _phase[channel] -= 2f * MathF.PI;

                var delaySamples = (baseDelay + depth * (0.5f + 0.5f * MathF.Sin(_phase[channel]))) * sr;
                var wet = ReadInterpolated(_delay, idx, delaySamples, capacity);

                buffer[i + channel] = buffer[i + channel] * dry + wet * mix;
            }
        }
    }

    private static float ReadInterpolated(float[] delay, int head, float delaySamples, int capacity)
    {
        var frames = capacity / 2;
        var read = (head / 2 - delaySamples) % frames;
        if (read < 0)
            read += frames;

        var floor = (int)read;
        var fraction = read - floor;
        var next = floor + 1;
        if (next >= frames)
            next = 0;

        var a = delay[floor * 2];
        var b = delay[next * 2];
        return a + (b - a) * fraction;
    }

    public float GetParameter(int index) => index switch
    {
        0 => _mix,
        1 => _rate,
        2 => _depth,
        3 => _baseDelay,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _mix = Math.Clamp(value, 0f, 1f);
                break;
            case 1:
                _rate = Math.Clamp(value, 0.05f, 8f);
                break;
            case 2:
                _depth = Math.Clamp(value, 0f, 0.02f);
                break;
            case 3:
                _baseDelay = Math.Clamp(value, 0.001f, 0.08f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public int GetParameterIndex(string name)
    {
        if (EffectParameterNames.Equals(name, "Mix"))
            return 0;
        if (EffectParameterNames.Equals(name, "Rate"))
            return 1;
        if (EffectParameterNames.Equals(name, "Depth"))
            return 2;
        if (EffectParameterNames.Equals(name, "Delay"))
            return 3;
        return -1;
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _head = 0;
        _phase[0] = 0f;
        _phase[1] = MathF.PI / 2f;
    }
}