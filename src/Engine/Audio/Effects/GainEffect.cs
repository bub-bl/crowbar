namespace Crowbar.Engine.Audio;

/// <summary>
/// Gain linéaire avec lissage par échantillon : évite le zipper noise quand le
/// volume change entre deux blocs. Le paramètre <c>0</c> est le gain cible
/// (linéaire, 1 = pas de changement, 0 = silence).
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

        // Lissage exponentiel : convergence de ~10 ms.
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
