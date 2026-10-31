using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Veldrid;

/// <summary>An element number in a typed uniform arena, never a byte offset.</summary>
public readonly record struct UniformArenaElement(uint Value);

/// <summary>Marks a UniformBlock field whose bound range can select an element of typed arena storage.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class DynamicUniformAttribute : Attribute { }

/// <summary>Fixed-capacity, aligned uniform storage. Stage typed values, upload once, select elements through named UniformBlock fields.</summary>
public sealed class ShaderUniformArena<T> : IDisposable where T : unmanaged
{
    internal DeviceBuffer Buffer { get; }
    internal ResourceFactory Factory { get; }
    internal string BlockName { get; }
    public uint Capacity { get; }
    public uint StrideBytes { get; }
    public uint SizeInBytes => Buffer.SizeInBytes;
    private readonly byte[] staging;
    private bool disposed;

    internal ShaderUniformArena(GraphicsDevice device, string blockName, uint blockSize, uint capacity)
    {
        if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (Unsafe.SizeOf<T>() != blockSize) throw new ArgumentException("Uniform arena element size differs from verified block size.");
        Factory = device.ResourceFactory;
        BlockName = blockName;
        Capacity = capacity;
        StrideBytes = ComputeStride(blockSize, device.UniformBufferMinOffsetAlignment);
        uint size = checked(StrideBytes * capacity);
        staging = new byte[checked((int)size)];
        // Full-buffer, sequenced uploads. Do not use WRITE_DISCARD per draw.
        Buffer = Factory.CreateBuffer(new BufferDescription(size, BufferUsage.UniformBuffer));
    }

    internal static uint ComputeStride(uint blockSize, uint alignment)
    {
        if (blockSize == 0 || alignment == 0) throw new ArgumentOutOfRangeException();
        return checked(((blockSize + alignment - 1) / alignment) * alignment);
    }

    internal uint OffsetOf(UniformArenaElement element)
    {
        EnsureAlive();
        if (element.Value >= Capacity) throw new ArgumentOutOfRangeException(nameof(element));
        return checked(element.Value * StrideBytes);
    }

    public void Stage(UniformArenaElement element, in T value)
    {
        uint offset = OffsetOf(element);
        MemoryMarshal.Write(staging.AsSpan(checked((int)offset), Unsafe.SizeOf<T>()), in value);
    }

    /// <summary>One upload for the entire segment. Previously recorded draws retain the preceding contents according to command order.</summary>
    public void Upload(CommandList commands)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(commands);
        commands.UpdateBuffer(Buffer, 0, staging);
    }

    internal void EnsureAlive()
    {
        if (disposed || Buffer.IsDisposed) throw new ObjectDisposedException(nameof(ShaderUniformArena<T>));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Buffer.Dispose();
    }
}