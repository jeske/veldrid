using System;
using System.Runtime.CompilerServices;

namespace Veldrid;

/// <summary>
///     ONE typed uniform buffer that several <see cref="UniformBlock{T}"/> slots - on different shapes - bind at once.
///     Created from a verified block with <see cref="UniformBlock{T}.CreateSharedBuffer"/>; bound into each set through that set's
///     block <see cref="UniformBlock{T}.With(ShaderUniformBuffer{T})"/>. Each destination block was
///     name/type-verified against <typeparamref name="T"/> at <c>Bind</c>, so sharing adds no unchecked mapping.
///     <para>
///     Writes happen ONLY through <see cref="CommandList.UpdateBuffer{T}(ShaderUniformBuffer{T}, in T)"/> = exactly one raw UpdateBuffer:
///     command-ordered, one upload regardless of how many shapes bind the storage. There is no device-level write path.
///     The raw <see cref="DeviceBuffer"/> is never exposed. Allocation (Dynamic or not) is the creator's explicit choice.
///     </para>
/// </summary>
public sealed class ShaderUniformBuffer<T> : IDisposable where T : unmanaged
{
    internal DeviceBuffer Buffer { get; }
    internal ResourceFactory Factory { get; }
    internal string CreatingBlockName { get; }
    public uint SizeInBytes => Buffer.SizeInBytes;
    /// <summary>True once <see cref="Dispose"/> ran (or the underlying buffer was destroyed, e.g. device loss). Same contract as <see cref="DeviceBuffer.IsDisposed"/>.</summary>
    public bool IsDisposed => disposed || Buffer.IsDisposed;
    private bool disposed;

    internal ShaderUniformBuffer(ResourceFactory factory, string creatingBlockName, uint blockSize, BufferUsage usage)
    {
        TypedBufferUsageValidation.RequireUniform(usage);
        if (Unsafe.SizeOf<T>() != blockSize)
            throw new ArgumentException($"Shared uniform storage for '{creatingBlockName}': managed size {Unsafe.SizeOf<T>()} B differs from the verified block size {blockSize} B.");
        Factory = factory;
        CreatingBlockName = creatingBlockName;
        // Allocation is the caller's choice; no CPU shadow, no device-level path.
        Buffer = factory.CreateBuffer(new BufferDescription(blockSize, usage));
    }

    internal void EnsureAlive()
    {
        if (disposed || Buffer.IsDisposed) throw new ObjectDisposedException(nameof(ShaderUniformBuffer<T>));
    }

    /// <summary>Disposes the one buffer. Slots that shared it fail their live-resource check afterwards; blocks never dispose shared storage themselves.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Buffer.Dispose();
    }
}