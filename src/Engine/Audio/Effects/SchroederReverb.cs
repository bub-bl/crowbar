namespace Crowbar.Engine.Audio;

/// <summary>
/// Réverbération de Schroeder : quatre filtres en peigne en parallèle suivis de
/// deux filtres passe-tout en série, avec amortissement dans la boucle des
/// peignes. Lignes de délai par canal, pré-allouées à la construction.
///
/// Paramètres : <c>0</c> = mix humide (0..1), <c>1</c> = taille de la pièce
/// (0..1), <c>2</c> = amortissement (0..1), <c>3</c> = feedback (0..1).
/// </summary>
public sealed class SchroederReverb : IAudioEffect
{
    private const int CombCount = 4;
    private const int AllPassCount = 2;

    // Délais de base (échantillons @ 48 kHz), valeurs classiques de Schroeder.
    private static readonly int[] CombDelays = [1557, 1617, 1491, 1422];
    private static readonly int[] AllPassDelays = [225, 556];

    private volatile float _wet = 0.3f;
    private volatile float _roomSize = 0.6f;
    private volatile float _damping = 0.3f;
    private volatile float _feedback = 0.84f;

    private readonly int _sampleRate;
    private readonly float[][] _combL;
    private readonly float[][] _combR;
    private readonly float[][] _allPassL;
    private readonly float[][] _allPassR;
    private readonly int[] _combIndices = new int[CombCount];
    private readonly int[] _allPassIndices = new int[AllPassCount];
    private readonly float[] _combFilterStateL = new float[CombCount];
    private readonly float[] _combFilterStateR = new float[CombCount];

    public SchroederReverb(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        _combL = new float[CombCount][];
        _combR = new float[CombCount][];
        _allPassL = new float[AllPassCount][];
        _allPassR = new float[AllPassCount][];

        for (var i = 0; i < CombCount; i++)
        {
            // La taille de la pièce peut étirer le délai jusqu'à ×1,5 : on
            // dimensionne pour le maximum afin de ne jamais déborder.
            var size = DelaySamples(CombDelays[i], 1.5f);
            _combL[i] = new float[size];
            _combR[i] = new float[size];
        }
        for (var i = 0; i < AllPassCount; i++)
        {
            var size = DelaySamples(AllPassDelays[i], 1.5f);
            _allPassL[i] = new float[size];
            _allPassR[i] = new float[size];
        }
    }

    public int ParameterCount => 4;

    public void Process(Span<float> buffer, AudioEffectContext context)
    {
        var wet = Math.Clamp(_wet, 0f, 1f);
        var dry = 1f - wet;
        var damping = Math.Clamp(_damping, 0f, 1f);
        var feedback = Math.Clamp(_feedback, 0f, 0.99f);

        for (var i = 0; i < buffer.Length; i += 2)
        {
            var inputL = buffer[i];
            var inputR = buffer[i + 1];

            var wetL = 0f;
            var wetR = 0f;
            for (var c = 0; c < CombCount; c++)
            {
                wetL += ProcessComb(c, inputL, damping, feedback, _combL[c], ref _combFilterStateL[c], ref _combIndices[c]);
                wetR += ProcessComb(c, inputR, damping, feedback, _combR[c], ref _combFilterStateR[c], ref _combIndices[c]);
            }

            // Les peignes sont en parallèle : moyenne simple.
            wetL /= CombCount;
            wetR /= CombCount;

            for (var a = 0; a < AllPassCount; a++)
            {
                wetL = ProcessAllPass(a, wetL, _allPassL[a], ref _allPassIndices[a]);
                wetR = ProcessAllPass(a, wetR, _allPassR[a], ref _allPassIndices[a]);
            }

            buffer[i] = inputL * dry + wetL * wet;
            buffer[i + 1] = inputR * dry + wetR * wet;
        }
    }

    public float GetParameter(int index) => index switch
    {
        0 => _wet,
        1 => _roomSize,
        2 => _damping,
        3 => _feedback,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetParameter(int index, float value)
    {
        switch (index)
        {
            case 0:
                _wet = Math.Clamp(value, 0f, 1f);
                break;
            case 1:
                _roomSize = Math.Clamp(value, 0f, 1f);
                break;
            case 2:
                _damping = Math.Clamp(value, 0f, 1f);
                break;
            case 3:
                _feedback = Math.Clamp(value, 0f, 0.99f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Reset()
    {
        foreach (var line in _combL)
            Array.Clear(line);
        foreach (var line in _combR)
            Array.Clear(line);
        foreach (var line in _allPassL)
            Array.Clear(line);
        foreach (var line in _allPassR)
            Array.Clear(line);
        Array.Clear(_combIndices);
        Array.Clear(_allPassIndices);
        Array.Clear(_combFilterStateL);
        Array.Clear(_combFilterStateR);
    }

    private int DelaySamples(int baseDelay, float roomScale) =>
        Math.Max(8, (int)(baseDelay * (_sampleRate / 48000f) * roomScale));

    private float ProcessComb(int index, float input, float damping, float feedback, float[] line, ref float filterState, ref int position)
    {
        var delay = DelaySamples(CombDelays[index], 0.5f + _roomSize);
        var delayed = line[position];
        filterState = delayed * (1f - damping) + filterState * damping;
        line[position] = input + filterState * feedback;
        position = (position + 1) % delay;
        return delayed;
    }

    private static float ProcessAllPass(int index, float input, float[] line, ref int position)
    {
        var delayed = line[position];
        var output = -input + delayed;
        line[position] = input + delayed * 0.5f;
        position = (position + 1) % line.Length;
        return output;
    }
}
