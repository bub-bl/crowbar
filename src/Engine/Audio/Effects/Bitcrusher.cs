namespace Crowbar.Engine.Audio;

/// <summary>
/// Lo-fi bitcrusher: reduces the sample rate (each held frame is repeated
/// <c>Downsample</c> times) and the bit depth (uniform quantization), producing
/// the aliasing and grain of early digital hardware.
///
/// Parameters: <c>0</c> = downsample (1..16), <c>1</c> = bits (1..16).
/// </summary>
public sealed class Bitcrusher : IAudioEffect
{
    private volatile float _downsample = 4f;
    private volatile float _bits = 8f;

    private float _heldL;
    private float _heldR;
    private int _phase;

    public int ParameterCount => 2;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var downsample = Math.Max(1, (int)Math.Clamp(_downsample, 1f, 16f));
        var bits = Math.Max(1, (int)Math.Clamp(_bits, 1f, 16f));
        var steps = 1 << Math.Min(bits - 1, 14); // capped to avoid overflow.
        var inverse = 1f / steps;

        for (var i = 0; i < buffer.Length; i += 2)
        {
            if (_phase == 0)
            {
                _heldL = buffer[i];
                _heldR = buffer[i + 1];
            }
            _phase++;
            if (_phase >= downsample)
                _phase = 0;

            buffer[i] = MathF.Round(_heldL * steps) * inverse;
            buffer[i + 1] = MathF.Round(_heldR * steps) * inverse;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _downsample,
        1 => _bits,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _downsample = Math.Clamp(value, 1f, 16f);
                break;
            case 1:
                _bits = Math.Clamp(value, 1f, 16f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public int GetParameterIndex(string name)
    {
        if (EffectParameterNames.Equals(name, "Downsample"))
            return 0;
        if (EffectParameterNames.Equals(name, "Bits"))
            return 1;
        return -1;
    }

    public void Reset()
    {
        _heldL = 0f;
        _heldR = 0f;
        _phase = 0;
    }
}