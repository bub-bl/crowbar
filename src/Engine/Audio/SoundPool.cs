namespace Crowbar.Engine.Audio;

/// <summary>Cross-thread state of a pool slot.</summary>
internal enum SoundState : int
{
    /// <summary>Slot is free.</summary>
    Free = 0,
    /// <summary>Reserved by the game thread, waiting for DSP activation.</summary>
    Reserved = 1,
    /// <summary>Active: the DSP renders it every block.</summary>
    Active = 2
}

/// <summary>
/// Fixed-size sound pool. The game thread reserves a slot (and can steal the
/// lowest-priority sound when the pool is full); the DSP thread activates the
/// sound when it processes the <c>Play</c> command and frees the slot when the
/// sound ends. Only <see cref="AudioSound.State"/> and
/// <see cref="AudioSound.Generation"/> are shared (atomic volatile writes); the
/// rest of the sound belongs to the DSP.
/// </summary>
internal sealed class SoundPool
{
    public const int Capacity = 32;

    private readonly AudioSound[] _sounds = new AudioSound[Capacity];

    public SoundPool()
    {
        for (var i = 0; i < Capacity; i++)
            _sounds[i] = new AudioSound();
    }

    public AudioSound this[int index] => _sounds[index];

    /// <summary>
    /// Reserves a slot from the game thread: first a free slot, otherwise the
    /// active/reserved sound with the lowest priority. Returns the slot index
    /// and its new generation.
    /// </summary>
    public (int Slot, int Generation) Reserve(int priority)
    {
        var slot = -1;
        for (var i = 0; i < Capacity; i++)
        {
            if (_sounds[i].State == (int)SoundState.Free)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            // Pool full: steal the lowest-priority sound (or the first one as a
            // fallback). The priority read here can be slightly stale, which is
            // an acceptable trade-off for a heuristic.
            var lowest = int.MaxValue;
            for (var i = 0; i < Capacity; i++)
            {
                if (_sounds[i].Priority < lowest)
                {
                    lowest = _sounds[i].Priority;
                    slot = i;
                }
            }
        }

        var sound = _sounds[slot];
        sound.State = (int)SoundState.Reserved;
        sound.Generation++;
        return (slot, sound.Generation);
    }
}
