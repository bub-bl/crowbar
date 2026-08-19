namespace Crowbar.Engine.Audio;

/// <summary>
/// Panoramique equal-power stéréo. Le paramètre <c>0</c> est la position dans le
/// champ stéréo, de <c>-1</c> (tout à gauche) à <c>+1</c> (tout à droite),
/// <c>0</c> étant le centre. Les gains gauche/droite sont lissés par échantillon.
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
        // Snap sur la cible courante : Reset place l'effet directement dans son
        // état stationnaire (pas de lissage depuis le centre).
        var clamped = Math.Clamp(_pan, -1f, 1f);
        var angle = (clamped + 1f) * (MathF.PI / 4f);
        _currentLeft = MathF.Cos(angle);
        _currentRight = MathF.Sin(angle);
    }
}
