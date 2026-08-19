namespace Crowbar.Engine.Audio;

/// <summary>Cross-thread state of a pool slot.</summary>
internal enum VoiceState : int
{
    /// <summary>Slot is free.</summary>
    Free = 0,
    /// <summary>Reserved by the game thread, waiting for DSP activation.</summary>
    Reserved = 1,
    /// <summary>Active: the DSP renders it every block.</summary>
    Active = 2
}

/// <summary>
/// Fixed-size voice pool. The game thread reserves a slot (and can steal the
/// lowest-priority voice when the pool is full); the DSP thread activates the
/// voice when it processes the <c>Play</c> command and frees the slot when the
/// voice ends. Only <see cref="AudioVoice.State"/> and
/// <see cref="AudioVoice.Generation"/> are shared (atomic volatile writes); the
/// rest of the voice belongs to the DSP.
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
    /// Reserves a slot from the game thread: first a free slot, otherwise the
    /// active/reserved voice with the lowest priority. Returns the slot index
    /// and its new generation.
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
            // Pool full: steal the lowest-priority voice (or the first one as a
            // fallback). The priority read here can be slightly stale, which is
            // an acceptable trade-off for a heuristic.
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
