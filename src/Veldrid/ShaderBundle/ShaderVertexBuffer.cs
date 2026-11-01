using System;
using System.Runtime.CompilerServices;

namespace Veldrid;

/// <summary>Vertex storage whose upload and binding type are identical; written through <see cref="CommandList.UpdateBuffer{TVertex}(ShaderVertexBuffer{TVertex}, uint, ReadOnlySpan{TVertex})"/>, bound through <see cref="CommandList.SetVertexBuffer{TVertex}(VertexInput{TVertex}, ShaderVertexBuffer{TVertex})"/>. The raw buffer is never exposed publicly.</summary>
public sealed class ShaderVertexBuffer<TVertex> : IDisposable where TVertex : unmanaged
{
    internal DeviceBuffer Buffer { get; }
    internal ResourceFactory Factory { get; }
    public uint Capacity { get; }
    private bool disposed;

    internal ShaderVertexBuffer(ResourceFactory factory, uint capacity, BufferUsage usage)
    {
        TypedBufferUsageValidation.RequireVertex(usage);
        if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Factory = factory;
        Capacity = capacity;
        uint size = checked(capacity * (uint)Unsafe.SizeOf<TVertex>());
        Buffer = factory.CreateBuffer(new BufferDescription(size, usage));
    }

    /// <summary>Byte size of the whole buffer (for consumers that preserve a full-buffer upload policy).</summary>
    public uint SizeInBytes => Buffer.SizeInBytes;
    /// <summary>True once <see cref="Dispose"/> ran (or the underlying buffer was destroyed, e.g. device loss). Same contract as <see cref="DeviceBuffer.IsDisposed"/>.</summary>
    public bool IsDisposed => disposed || Buffer.IsDisposed;
    /// <summary>The native-usage flags this storage was created with.</summary>
    public BufferUsage Usage => Buffer.Usage;

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