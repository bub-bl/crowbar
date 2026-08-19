namespace Crowbar.Engine.Audio;

/// <summary>
/// Stereo equal-power panning. Parameter <c>0</c> is the position in the stereo
/// field, from <c>-1</c> (hard left) to <c>+1</c> (hard right), with <c>0</c> in
/// the center. The left/right gains are smoothed per sample.
/// </summary>
public sealed class PanEffect : IAudioEffect
{
    private volatile float _pan;
    private float _currentLeft = MathF.Sqrt(0.5f);
    private float _currentRight = MathF.Sqrt(0.5f);

    public int ParameterCount => 1;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var clamped = Math.Clamp(_pan, -1f, 1f);
        var angle = (clamped + 1f) * (MathF.PI / 4f);
        var targetLeft = MathF.Cos(angle);
        var targetRight = MathF.Sin(angle);

        var coefficient = MathF.Pow(0.0001f, 1f / (context.SampleRate * 0.01f));
        for (var i = 0; i < buffer.Length; i += 2)
        {
            _currentLeft += (targetLeft - _currentLeft) * coefficient;
            _currentRight += (targetRight - _currentRight) * coefficient;
            buffer[i] *= _currentLeft;
            buffer[i + 1] *= _currentRight;
        }
    }

    public float GetParameter(int index) => index == 0 ? _pan : throw new ArgumentOutOfRangeException(nameof(index));

    public void SetParameter(int index, float value)
    {
        if (index != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        _pan = value;
    }

    public void Reset()
    {
        // Snap to the current target: Reset places the effect directly in its
        // steady state (no smoothing from the center).
        var clamped = Math.Clamp(_pan, -1f, 1f);
        var angle = (clamped + 1f) * (MathF.PI / 4f);
        _currentLeft = MathF.Cos(angle);
        _currentRight = MathF.Sin(angle);
    }
}
