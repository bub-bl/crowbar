namespace Crowbar.Engine.Audio;

/// <summary>
/// Simple delay/echo with feedback: <c>y[n] = x[n] + wet * x[n - d]</c> with the
/// loop <c>x[n - d] = x[n] + feedback * x[n - d]</c>. One delay line per
/// channel, pre-allocated for a maximum duration of 2 seconds.
///
/// Parameters: <c>0</c> = delay time (s), <c>1</c> = feedback (0..1),
/// <c>2</c> = wet mix (0..1).
/// </summary>
public sealed class DelayEffect : IAudioEffect
{
    private const float MaxDelaySeconds = 2f;

    private volatile float _delaySeconds = 0.3f;
    private volatile float _feedback = 0.35f;
    private volatile float _wet = 0.25f;

    private readonly int _sampleRate;
    private readonly float[] _left;
    private readonly float[] _right;
    private int _writeIndex;

    public DelayEffect(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        var size = (int)(MaxDelaySeconds * sampleRate);
        _left = new float[size];
        _right = new float[size];
    }

    public int ParameterCount => 3;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var delaySamples = Math.Clamp((int)(_delaySeconds * _sampleRate), 1, _left.Length);
        var feedback = Math.Clamp(_feedback, 0f, 0.99f);
        var wet = Math.Clamp(_wet, 0f, 1f);

        for (var i = 0; i < buffer.Length; i += 2)
        {
            var readIndex = _writeIndex - delaySamples;
            if (readIndex < 0)
                readIndex += _left.Length;

            var delayedL = _left[readIndex];
            var delayedR = _right[readIndex];

            var inputL = buffer[i];
            var inputR = buffer[i + 1];

            _left[_writeIndex] = inputL + delayedL * feedback;
            _right[_writeIndex] = inputR + delayedR * feedback;
            _writeIndex = (_writeIndex + 1) % _left.Length;

            buffer[i] = inputL + delayedL * wet;
            buffer[i + 1] = inputR + delayedR * wet;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _delaySeconds,
        1 => _feedback,
        2 => _wet,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _delaySeconds = Math.Clamp(value, 0.001f, MaxDelaySeconds);
                break;
            case 1:
                _feedback = Math.Clamp(value, 0f, 0.99f);
                break;
            case 2:
                _wet = Math.Clamp(value, 0f, 1f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public int GetParameterIndex(string name)
    {
        if (EffectParameterNames.Equals(name, "Delay"))
            return 0;
        if (EffectParameterNames.Equals(name, "Feedback"))
            return 1;
        if (EffectParameterNames.Equals(name, "Wet"))
            return 2;
        return -1;
    }

    public void Reset()
    {
        Array.Clear(_left);
        Array.Clear(_right);
        _writeIndex = 0;
    }
}
