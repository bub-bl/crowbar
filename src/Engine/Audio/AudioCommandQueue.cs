using System.Numerics;

namespace Crowbar.Engine.Audio;

/// <summary>Type de commande échangée entre le thread de jeu et le thread DSP.</summary>
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
/// Une commande audio. Les champs sont surchargés selon
/// <see cref="AudioCommandType"/> ; la structure reste de taille fixe, allouée
/// une fois pour toute dans le ring du producteur.
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
/// File de commandes SPSC (single producer / single consumer) sans verrou : le
/// thread de jeu produit, le thread DSP consomme. Le ring est pré-dimensionné ;
/// un producteur plus rapide que le consommateur patiente en spin (le DSP vide
/// la file à chaque bloc, donc la situation est transitoire). Aucune allocation
/// en régime permanent.
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

    /// <summary>Nombre de commandes en attente (diagnostic).</summary>
    public int Count => Volatile.Read(ref _head) - Volatile.Read(ref _tail);

    /// <summary>Ajoute une commande (appelé uniquement depuis le thread de jeu).</summary>
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

    /// <summary>Dépile une commande, ou retourne false quand la file est vide (DSP uniquement).</summary>
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

    /// <summary>Vide la file (à n'appeler que lorsque le DSP est arrêté).</summary>
    public void Clear()
    {
        while (TryDequeue(out _))
        {
        }
    }
}
