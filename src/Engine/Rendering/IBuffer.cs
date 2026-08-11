namespace Crowbar.Engine.Rendering;

/// <summary>
/// Backend-neutral GPU buffer. Written from the CPU through the queue; the
/// backend picks the right upload path (QueueWriteBuffer or a staging copy).
/// </summary>
public interface IBuffer : IDisposable
{
    ulong Size { get; }

    void Write<T>(in T data, ulong offset = 0) where T : unmanaged;

    void Write(ReadOnlySpan<byte> data, ulong offset = 0);
}
