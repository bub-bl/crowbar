namespace Crowbar.Engine.Audio;

/// <summary>Type de filtre biquad (RBJ cookbook).</summary>
public enum BiquadType
{
    LowPass = 0,
    HighPass = 1,
    Peaking = 2
}

/// <summary>
/// Biquad RBJ (passe-bas, passe-haut, paramétrique/peaking) en forme directe
/// transposée II, avec un état par canal. Les coefficients ne sont recalculés
/// que lorsqu'un paramètre change (drapeau volatil), donc le chemin DSP en
/// régime permanent est une simple récurrence, sans allocation ni trigonométrie.
///
/// Paramètres : <c>0</c> = type (<see cref="BiquadType"/>), <c>1</c> = fréquence
/// (Hz), <c>2</c> = Q, <c>3</c> = gain (dB, peaking uniquement).
/// </summary>
public sealed class BiquadFilter : IAudioEffect
{
    private volatile int _type;
    private volatile float _frequency;
    private volatile float _q;
    private volatile float _gainDb;

    private volatile bool _dirty = true;
    private float _b0, _b1, _b2, _a1, _a2;
    private readonly float[] _z1 = new float[2];
    private readonly float[] _z2 = new float[2];

    public BiquadFilter(BiquadType type = BiquadType.LowPass, float frequency = 1000f, float q = 0.707f, float gainDb = 0f)
    {
        _type = (int)type;
        _frequency = frequency;
        _q = q;
        _gainDb = gainDb;
    }

    public int ParameterCount => 4;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        if (_dirty)
            RecomputeCoefficients(context.SampleRate);

        var b0 = _b0;
        var b1 = _b1;
        var b2 = _b2;
        var a1 = _a1;
        var a2 = _a2;

        for (var i = 0; i < buffer.Length; i += 2)
        {
            var left = buffer[i];
            var right = buffer[i + 1];

            var outLeft = b0 * left + _z1[0];
            _z1[0] = b1 * left - a1 * outLeft + _z2[0];
            _z2[0] = b2 * left - a2 * outLeft;

            var outRight = b0 * right + _z1[1];
            _z1[1] = b1 * right - a1 * outRight + _z2[1];
            _z2[1] = b2 * right - a2 * outRight;

            buffer[i] = outLeft;
            buffer[i + 1] = outRight;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _type,
        1 => _frequency,
        2 => _q,
        3 => _gainDb,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _type = (int)value;
                break;
            case 1:
                _frequency = Math.Clamp(value, 1f, 24000f);
                break;
            case 2:
                _q = Math.Clamp(value, 0.01f, 40f);
                break;
            case 3:
                _gainDb = Math.Clamp(value, -40f, 40f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
        _dirty = true;
    }

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
        _dirty = true;
    }

    private void RecomputeCoefficients(int sampleRate)
    {
        _dirty = false;

        var type = (BiquadType)_type;
        var frequency = Math.Clamp(_frequency, 1f, sampleRate * 0.45f);
        var q = Math.Clamp(_q, 0.01f, 40f);

        var omega = 2f * MathF.PI * frequency / sampleRate;
        var cos = MathF.Cos(omega);
        var sin = MathF.Sin(omega);
        var alpha = sin / (2f * q);

        switch (type)
        {
            case BiquadType.LowPass:
            {
                var a0 = 1f + alpha;
                _b0 = (1f - cos) / 2f / a0;
                _b1 = (1f - cos) / a0;
                _b2 = _b0;
                _a1 = -2f * cos / a0;
                _a2 = (1f - alpha) / a0;
                break;
            }
            case BiquadType.HighPass:
            {
                var a0 = 1f + alpha;
                _b0 = (1f + cos) / 2f / a0;
                _b1 = -(1f + cos) / a0;
                _b2 = _b0;
                _a1 = -2f * cos / a0;
                _a2 = (1f - alpha) / a0;
                break;
            }
            default:
            {
                var gain = MathF.Pow(10f, _gainDb / 40f);
                var a0 = 1f + alpha / gain;
                _b0 = (1f + alpha * gain) / a0;
                _b1 = -2f * cos / a0;
                _b2 = (1f - alpha * gain) / a0;
                _a1 = -2f * cos / a0;
                _a2 = (1f - alpha / gain) / a0;
                break;
            }
        }
    }
}
