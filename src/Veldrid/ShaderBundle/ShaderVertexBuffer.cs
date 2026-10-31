using System;
using System.Runtime.CompilerServices;

namespace Veldrid;

/// <summary>Vertex storage whose upload and binding type are identical. The raw buffer is never exposed publicly.</summary>
public sealed class ShaderVertexBuffer<TVertex> : IDisposable where TVertex : unmanaged
{
    internal DeviceBuffer Buffer { get; }
    internal ResourceFactory Factory { get; }
    public uint Capacity { get; }
    private bool disposed;

    internal ShaderVertexBuffer(ResourceFactory factory, uint capacity)
    {
        if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Factory = factory;
        Capacity = capacity;
        uint size = checked(capacity * (uint)Unsafe.SizeOf<TVertex>());
        Buffer = factory.CreateBuffer(new BufferDescription(size, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
    }

    /// <summary>Upload elements, not anonymously typed bytes. The offset is measured in vertices.</summary>
    public void Update(CommandList commands, ReadOnlySpan<TVertex> vertices, uint firstVertex = 0)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(commands);
        if (firstVertex > Capacity || (uint)vertices.Length > Capacity - firstVertex)
            throw new ArgumentOutOfRangeException(nameof(firstVertex));
        commands.UpdateBuffer(Buffer, checked(firstVertex * (uint)Unsafe.SizeOf<TVertex>()), vertices);
    }

    internal void EnsureAlive()
    {
        if (disposed || Buffer.IsDisposed) throw new ObjectDisposedException(nameof(ShaderVertexBuffer<TVertex>));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Buffer.Dispose();
    }
}