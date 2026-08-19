namespace Crowbar.Engine.Audio;

/// <summary>État cross-thread d'un slot du pool.</summary>
internal enum VoiceState : int
{
    /// <summary>Slot libre.</summary>
    Free = 0,
    /// <summary>Réservé par le thread de jeu, en attente d'activation par le DSP.</summary>
    Reserved = 1,
    /// <summary>Actif : le DSP le rend à chaque bloc.</summary>
    Active = 2
}

/// <summary>
/// Pool de voix de taille fixe. Le thread de jeu réserve un slot (et peut voler
/// la voix la moins prioritaire quand le pool est plein) ; le thread DSP active
/// la voix quand il traite la commande <c>Play</c> et libère le slot quand la
/// voix se termine. Seuls <see cref="AudioVoice.State"/> et
/// <see cref="AudioVoice.Generation"/> sont partagés (écritures volatiles
/// atomiques) ; le reste de la voix appartient au DSP.
/// </summary>
internal sealed class VoicePool
{
    public const int Capacity = 32;

    private readonly AudioVoice[] _voices = new AudioVoice[Capacity];

    public VoicePool()
    {
        for (var i = 0; i < Capacity; i++)
            _voices[i] = new AudioVoice();
    }

    public AudioVoice this[int index] => _voices[index];

    /// <summary>
    /// Réserve un slot depuis le thread de jeu : d'abord un slot libre, sinon la
    /// voix active/réservée de plus basse priorité. Retourne l'index du slot et
    /// sa nouvelle génération.
    /// </summary>
    public (int Slot, int Generation) Reserve(int priority)
    {
        var slot = -1;
        for (var i = 0; i < Capacity; i++)
        {
            if (_voices[i].State == (int)VoiceState.Free)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            // Pool plein : on vole la voix de plus basse priorité (à défaut la
            // première). La priorité lue ici peut être légèrement en retard,
            // ce qui est un compromis acceptable pour une heuristique.
            var lowest = int.MaxValue;
            for (var i = 0; i < Capacity; i++)
            {
                if (_voices[i].Priority < lowest)
                {
                    lowest = _voices[i].Priority;
                    slot = i;
                }
            }
        }

        var voice = _voices[slot];
        voice.State = (int)VoiceState.Reserved;
        voice.Generation++;
        return (slot, voice.Generation);
    }
}
