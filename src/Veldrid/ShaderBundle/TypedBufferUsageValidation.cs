using System;

namespace Veldrid;

// Creation-time validation is unconditional, including Release builds.
// These helpers cover the existing uniform/vertex storage wrappers, not all native buffer roles.
internal static class TypedBufferUsageValidation
{
    internal static void RequireUniform(BufferUsage usage)
    {
        if (usage != BufferUsage.UniformBuffer && usage != (BufferUsage.UniformBuffer | BufferUsage.Dynamic))
            throw new ArgumentException("Typed uniform storage requires UniformBuffer, optionally combined with Dynamic. Staging and other binding roles are not uniform storage.", nameof(usage));
    }

    internal static void RequireVertex(BufferUsage usage)
    {
        if (usage != BufferUsage.VertexBuffer && usage != (BufferUsage.VertexBuffer | BufferUsage.Dynamic))
            throw new ArgumentException("Typed vertex storage requires VertexBuffer, optionally combined with Dynamic.", nameof(usage));
    }
}