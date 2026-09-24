using System.Threading;

namespace Veldrid
{
    /// <summary>
    ///     Process-wide counters for the CPU-visible STAGING BUFFER pools that back <see cref="CommandList.UpdateBuffer(DeviceBuffer, uint, System.IntPtr, uint)" />
    ///     and <see cref="GraphicsDevice.UpdateBuffer(DeviceBuffer, uint, System.IntPtr, uint)" /> when a direct Map/UpdateSubresource path is not
    ///     available (D3D11: partial update of a Dynamic buffer; Vulkan: every UpdateBuffer through a command list; Metal: likewise).
    ///     Every pool site (D3D11CommandList, D3D11GraphicsDevice, VkCommandList, VkGraphicsDevice, MTLCommandList) reports here.
    ///     <para>
    ///         Why this exists (2026-09-24): a caller whose upload size grew a little every frame made these pools allocate one
    ///         never-released buffer per new high-water mark — 2.1 GB in AN_Monitor — and nothing in the process could see it.
    ///         Hosts publish these into their telemetry (FluidUI: <c>Fluid.GpuStaging*</c> varz). A monotonically rising
    ///         <see cref="CreatedTotalCount" /> with rising <see cref="PooledBytes" /> is that bug; a flat CreatedTotalCount is health.
    ///     </para>
    ///     Snapshot reads are lock-free and may be torn across fields by one update; that is acceptable for telemetry.
    /// </summary>
    public static class StagingBufferPoolTelemetry
    {
        private static long pooledCount;
        private static long pooledBytes;
        private static long createdTotalCount;
        private static long createdTotalBytes;
        private static long evictedTotalCount;
        private static long evictedTotalBytes;

        /// <summary>Staging buffers currently idle in a pool (allocated, not in flight).</summary>
        public static long PooledCount => Interlocked.Read(ref pooledCount);

        /// <summary>Bytes held by idle pooled staging buffers — the memory a leak in these pools shows up as.</summary>
        public static long PooledBytes => Interlocked.Read(ref pooledBytes);

        /// <summary>Staging buffers ever created by any pool (cumulative). Rising without bound = the pool is not reusing.</summary>
        public static long CreatedTotalCount => Interlocked.Read(ref createdTotalCount);

        /// <summary>Bytes of staging buffers ever created (cumulative).</summary>
        public static long CreatedTotalBytes => Interlocked.Read(ref createdTotalBytes);

        /// <summary>Idle pooled buffers disposed because a larger request superseded them (cumulative). Expected to be small and to settle.</summary>
        public static long EvictedTotalCount => Interlocked.Read(ref evictedTotalCount);

        /// <summary>Bytes released by evictions (cumulative).</summary>
        public static long EvictedTotalBytes => Interlocked.Read(ref evictedTotalBytes);

        /// <summary>A pool created a new staging buffer (a miss). Pool sites call this right after CreateBuffer.</summary>
        internal static void NoteCreated(uint sizeInBytes)
        {
            Interlocked.Increment(ref createdTotalCount);
            Interlocked.Add(ref createdTotalBytes, sizeInBytes);
        }

        /// <summary>A buffer went back to an idle pool (GPU work that used it completed).</summary>
        internal static void NoteReturnedToPool(uint sizeInBytes)
        {
            Interlocked.Increment(ref pooledCount);
            Interlocked.Add(ref pooledBytes, sizeInBytes);
        }

        /// <summary>An idle pooled buffer was taken for a new upload.</summary>
        internal static void NoteTakenFromPool(uint sizeInBytes)
        {
            Interlocked.Decrement(ref pooledCount);
            Interlocked.Add(ref pooledBytes, -(long)sizeInBytes);
        }

        /// <summary>An idle pooled buffer was disposed (superseded by a larger request, or pool teardown).</summary>
        internal static void NoteEvictedFromPool(uint sizeInBytes)
        {
            Interlocked.Decrement(ref pooledCount);
            Interlocked.Add(ref pooledBytes, -(long)sizeInBytes);
            Interlocked.Increment(ref evictedTotalCount);
            Interlocked.Add(ref evictedTotalBytes, sizeInBytes);
        }
    }
}