using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>Command type exchanged between the game thread and the DSP thread.</summary>
internal enum AudioCommandType : byte
{
    Play,
    Stop,
    SetVolume,
    SetPitch,
    SetPan,
    FadeTo,
    SetPosition,
    AddEffect,
    SetEffectParam,
    StopAll
}

/// <summary>
/// An audio command. Fields are overloaded depending on
/// <see cref="AudioCommandType"/>; the struct stays fixed-size, allocated once
/// for all in the producer ring.
/// </summary>
internal struct AudioCommand
{
    public AudioCommandType Type;
    public int Slot;
    public int Generation;
    public IAudioSource? Source;
    public IAudioEffect? Effect;
    public AudioBus? Bus;
    public Vector3 Position;
    public float A, B, C, D, E;
    public int Param0, Param1;
}

/// <summary>
/// Lock-free SPSC (single producer / single consumer) command queue: the game
/// thread produces, the DSP thread consumes. The ring is pre-sized; a producer
/// faster than the consumer spins (the DSP drains the queue every block, so the
/// situation is transient). No allocation in steady state.
/// </summary>
internal sealed class AudioCommandQueue
{
    private readonly AudioCommand[] _ring;
    private readonly int _capacity;
    private int _head;
    private int _tail;

    public AudioCommandQueue(int capacity = 1024)
    {
        _capacity = Math.Max(16, capacity);
        _ring = new AudioCommand[_capacity];
    }

    /// <summary>Number of pending commands (diagnostic).</summary>
    public int Count => Volatile.Read(ref _head) - Volatile.Read(ref _tail);

    /// <summary>Adds a command (called only from the game thread).</summary>
    public void Enqueue(in AudioCommand command)
    {
        var spin = new SpinWait();
        while (true)
        {
            var head = Volatile.Read(ref _head);
            var tail = Volatile.Read(ref _tail);
            if (head - tail < _capacity)
            {
                _ring[head % _capacity] = command;
                Volatile.Write(ref _head, head + 1);
                return;
            }

            spin.SpinOnce();
            if (spin.Count % 1024 == 0)
                Thread.Sleep(0);
        }
    }

    /// <summary>Dequeues a command, or returns false when the queue is empty (DSP only).</summary>
    public bool TryDequeue(out AudioCommand command)
    {
        var head = Volatile.Read(ref _head);
        var tail = _tail;
        if (head == tail)
        {
            command = default;
            return false;
        }

        command = _ring[tail % _capacity];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>Empties the queue (only call once the DSP is stopped).</summary>
    public void Clear()
    {
        while (TryDequeue(out _))
        {
        }
    }
}
