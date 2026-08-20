namespace Crowbar.Engine.Audio;

/// <summary>
/// Symmetric soft-clipping waveshaper (tanh). Higher drive yields warmer
/// harmonic saturation that never hard-clips (the output is bounded by the
/// transfer curve).
///
/// Parameters: <c>0</c> = drive (0..10), <c>1</c> = mix (0..1).
/// </summary>
public sealed class Distortion : IAudioEffect
{
    private volatile float _drive = 3f;
    private volatile float _mix = 1f;

    public int ParameterCount => 2;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var drive = Math.Max(0f, _drive);
        var mix = Math.Clamp(_mix, 0f, 1f);
        var dry = 1f - mix;

        for (var i = 0; i < buffer.Length; i++)
        {
            var sample = buffer[i];
            var shaped = MathF.Tanh(drive * sample);
            buffer[i] = sample * dry + shaped * mix;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _drive,
        1 => _mix,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _drive = Math.Clamp(value, 0f, 10f);
                break;
            case 1:
                _mix = Math.Clamp(value, 0f, 1f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public int GetParameterIndex(string name)
    {
        if (EffectParameterNames.Equals(name, "Drive"))
            return 0;
        if (EffectParameterNames.Equals(name, "Mix"))
            return 1;
        return -1;
    }

    public void Reset()
    {
    }
}