namespace Crowbar.Engine.Audio;

/// <summary>
/// Swept short-delay flanger with regenerated feedback, mixed with the dry
/// signal. The delay sweeps from 0 to <c>Depth</c> seconds on an LFO.
///
/// Parameters: <c>0</c> = mix (0..1), <c>1</c> = rate (Hz),
/// <c>2</c> = depth (s), <c>3</c> = feedback (0..0.9).
/// </summary>
public sealed class Flanger : IAudioEffect
{
    private volatile float _mix = 0.5f;
    private volatile float _rate = 0.25f;
    private volatile float _depth = 0.0015f;
    private volatile float _feedback = 0.3f;

    private float[] _delay = [];
    private int _head;
    private float _phase;

    public int ParameterCount => 4;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var capacity = ((int)((_depth * context.SampleRate) + 16)) * 2;
        if (_delay.Length < capacity)
        {
            _delay = new float[capacity];
            _head = 0;
        }

        var sr = context.SampleRate;
        var mix = Math.Clamp(_mix, 0f, 1f);
        var dry = 1f - mix;
        var depth = Math.Max(0f, _depth);
        var feedback = Math.Clamp(_feedback, 0f, 0.9f);
        var phaseStep = 2f * MathF.PI * Math.Max(0.01f, _rate) / sr;

        for (var i = 0; i < buffer.Length; i += 2)
        {
            _phase += phaseStep;
            if (_phase > 2f * MathF.PI)
                _phase -= 2f * MathF.PI;

            var delaySamples = depth * (0.5f + 0.5f * MathF.Sin(_phase)) * sr;
            var frames = capacity / 2;

            var read = (_head / 2 - delaySamples) % frames;
            if (read < 0)
                read += frames;
            var floor = (int)read;
            var fraction = read - floor;
            var next = floor + 1 >= frames ? 0 : floor + 1;

            var idx = _head;
            for (var channel = 0; channel < 2; channel++)
            {
                var input = buffer[i + channel];
                var delayed = _delay[floor * 2 + channel] + (_delay[next * 2 + channel] - _delay[floor * 2 + channel]) * fraction;

                _delay[idx + channel] = input + delayed * feedback;
                buffer[i + channel] = input * dry + delayed * mix;
            }
            _head = (idx + 2) % _delay.Length;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _mix,
        1 => _rate,
        2 => _depth,
        3 => _feedback,
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
                _feedback = Math.Clamp(value, 0f, 0.9f);
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
        if (EffectParameterNames.Equals(name, "Feedback"))
            return 3;
        return -1;
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _head = 0;
        _phase = 0f;
    }
}