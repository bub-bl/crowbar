namespace Crowbar.Engine.Audio;

/// <summary>
/// Linear gain with per-sample smoothing: avoids zipper noise when the volume
/// changes between two blocks. Parameter <c>0</c> is the target gain (linear,
/// 1 = no change, 0 = silence).
/// </summary>
public sealed class GainEffect : IAudioEffect
{
    private volatile float _target = 1f;
    private float _current = 1f;

    public int ParameterCount => 1;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var target = _target;
        if (_current == target)
        {
            if (target == 1f)
                return;
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] *= target;
            return;
        }

        // Exponential smoothing: ~10 ms convergence.
        var coefficient = MathF.Pow(0.0001f, 1f / (context.SampleRate * 0.01f));
        for (var i = 0; i < buffer.Length; i++)
        {
            _current += (target - _current) * coefficient;
            buffer[i] *= _current;
        }
    }

    public float GetParameter(int index) => index == 0 ? _target : throw new ArgumentOutOfRangeException(nameof(index));

    public void SetParameter(int index, float value)
    {
        if (index != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        _target = Math.Clamp(value, 0f, 16f);
    }

    public void Reset() => _current = _target;
}
