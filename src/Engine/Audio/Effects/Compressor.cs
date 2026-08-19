namespace Crowbar.Engine.Audio;

/// <summary>
/// Compresseur/limiteur feed-forward à détection de crête, une enveloppe par
/// canal, avec temps d'attaque/relâchement et gain de maquillage. Utile en fin
/// de chaîne sur le bus master pour contenir les crêtes.
///
/// Paramètres : <c>0</c> = seuil (dB, négatif), <c>1</c> = ratio (&gt;= 1),
/// <c>2</c> = attaque (s), <c>3</c> = relâchement (s), <c>4</c> = gain de
/// maquillage (dB).
/// </summary>
public sealed class Compressor : IAudioEffect
{
    private volatile float _thresholdDb = -12f;
    private volatile float _ratio = 4f;
    private volatile float _attack = 0.01f;
    private volatile float _release = 0.15f;
    private volatile float _makeupDb = 0f;

    private readonly float[] _envelope = [1f, 1f];

    public int ParameterCount => 5;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var threshold = MathF.Pow(10f, _thresholdDb / 20f);
        var ratio = Math.Max(1f, _ratio);
        var attackCoef = MathF.Exp(-1f / (Math.Max(0.0001f, _attack) * context.SampleRate));
        var releaseCoef = MathF.Exp(-1f / (Math.Max(0.0001f, _release) * context.SampleRate));
        var makeup = MathF.Pow(10f, _makeupDb / 20f);

        for (var i = 0; i < buffer.Length; i += 2)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var sample = buffer[i + channel];
                var level = MathF.Abs(sample);
                var target = level <= threshold
                    ? 1f
                    : (threshold + (level - threshold) / ratio) / level;

                // Attaque quand on doit réduire, relâchement vers 1 sinon.
                var envelope = target < _envelope[channel]
                    ? _envelope[channel] * attackCoef + target * (1f - attackCoef)
                    : _envelope[channel] * releaseCoef + target * (1f - releaseCoef);
                _envelope[channel] = envelope;

                buffer[i + channel] = sample * envelope * makeup;
            }
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _thresholdDb,
        1 => _ratio,
        2 => _attack,
        3 => _release,
        4 => _makeupDb,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _thresholdDb = Math.Clamp(value, -60f, 0f);
                break;
            case 1:
                _ratio = Math.Clamp(value, 1f, 60f);
                break;
            case 2:
                _attack = Math.Clamp(value, 0.0001f, 1f);
                break;
            case 3:
                _release = Math.Clamp(value, 0.0001f, 5f);
                break;
            case 4:
                _makeupDb = Math.Clamp(value, 0f, 40f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Reset()
    {
        _envelope[0] = 1f;
        _envelope[1] = 1f;
    }
}
