using System;

namespace Veldrid
{
    /// <summary>
    ///     Outcome of <see cref="GraphicsDevice.TryResolveTimestamps"/>. The call never blocks; <see cref="NotReady"/> means "ask again next frame".
    /// </summary>
    public enum TimestampResolveResult
    {
        /// <summary>At least one slot in the range has not been written by the GPU yet (or the command list was never submitted). Ticks are untouched.</summary>
        NotReady = 0,

        /// <summary>Every slot in the range is available; <c>ticks</c> and <c>ticksPerSecond</c> are filled.</summary>
        Ready = 1,

        /// <summary>
        ///     Direct3D11 only: the GPU clock changed (power state / frequency) during the window covering these slots, or the range spans
        ///     windows with different frequencies. The values are meaningless; discard the range. Never returned on Vulkan.
        /// </summary>
        Disjoint = 2,
    }

    /// <summary>
    ///     A fixed-size set of GPU timestamp slots. <see cref="CommandList.WriteTimestamp(TimestampQueryPool, uint)"/> records a slot at a
    ///     point in a command list (end-of-pipe: everything recorded before it has finished executing); a few frames later
    ///     <see cref="GraphicsDevice.TryResolveTimestamps"/> reads the values back without blocking.
    ///     <para>
    ///         Ticks are DEVICE-ABSOLUTE: one clock for every pool on the device, so spans from different pools and frames lay on one
    ///         timeline. Vulkan values are already masked to the queue family's <c>timestampValidBits</c>.
    ///     </para>
    ///     <para>
    ///         A slot that is written again before its previous value was resolved abandons that value (counted in
    ///         <see cref="AbandonedWriteCount"/>); it is legal, never throws, and means the caller's ring is too shallow. Pools reused every
    ///         frame must therefore be ring-buffered by the CALLER (the automatic <see cref="GraphicsDevice.GpuTiming"/> layer does this for its
    ///         own pools). Slot reset is implicit and identical on every backend: a pool whose slots are not fresh is reset in full at the top
    ///         of the next command list that writes it; the first write to such a pool while a render pass is active throws under
    ///         <c>VALIDATE_USAGE</c> because Vulkan cannot reset inside a pass (<c>_TASKS/GpuTimestampQueries.md</c> 5.3).
    ///     </para>
    /// </summary>
    public abstract class TimestampQueryPool : IDeviceResource, IDisposable
    {
        /// <summary>Backend-only derivation: the per-slot state machine members below are internal.</summary>
        internal TimestampQueryPool() { }

        /// <summary>Number of slots; valid indices are <c>[0, Capacity)</c>.</summary>
        public abstract uint Capacity { get; }

        /// <summary>
        ///     Count of <see cref="CommandList.WriteTimestamp(TimestampQueryPool, uint)"/> calls that overwrote a slot whose previous value was
        ///     written but never resolved. Diagnostic only - a growing count means the caller's ring is too shallow.
        /// </summary>
        public abstract ulong AbandonedWriteCount { get; }

        /// <summary>A string identifying this instance. Can be used to differentiate between objects in graphics debuggers and other tools.</summary>
        public abstract string Name { get; set; }

        /// <summary>A bool indicating whether this instance has been disposed.</summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        ///     Marks written-but-never-resolved slots in <c>[first, first + count)</c> as consumed WITHOUT reading them, so the next
        ///     <see cref="CommandList.Begin"/> resets them and they become writable inside a render pass again. Used by the automatic
        ///     <see cref="GraphicsDevice.GpuTiming"/> layer for command lists that were recorded but never submitted. Fresh slots are left alone.
        /// </summary>
        internal abstract void DiscardRange(uint first, uint count);

        /// <summary>Frees unmanaged device resources controlled by this instance.</summary>
        public abstract void Dispose();
    }
}