using System;
using System.Collections.Generic;
using Vortice.Direct3D11;

namespace Veldrid.D3D11
{
    /// <summary>
    ///     One <c>D3D11_QUERY_TIMESTAMP_DISJOINT</c> bracket. D3D11 timestamp ticks are meaningless without the disjoint query that was
    ///     Begin()/End()'d around them on the SAME context: it supplies the tick frequency and the "clock changed, discard" flag. A deferred
    ///     context's command list is atomic, so the fork opens one window per <see cref="D3D11CommandList"/> recording (first
    ///     <c>WriteTimestamp</c>) and closes it at <c>End()</c>; every slot written in between remembers its window
    ///     (<see cref="D3D11TimestampQueryPool.SlotWindows"/>). Windows live in a device-level free list (<see cref="D3D11GraphicsDevice"/>)
    ///     and are returned when no slot references them any more. Decision B1 of <c>_TASKS/GpuTimestampQueries.md</c>.
    /// </summary>
    internal sealed class D3D11TimestampDisjointWindow
    {
        public readonly ID3D11Query Query;

        /// <summary>Set by <see cref="D3D11CommandList.End"/> / <c>Reset</c> once <c>DeviceContext.End(Query)</c> has been recorded; GetData before that is pointless.</summary>
        public bool Ended;

        /// <summary>Number of pool slots whose pending or resolved value belongs to this window. 0 and Ended => recyclable.</summary>
        public int SlotReferences;

        /// <summary>Cached result of the first successful GetData so later slot resolves in the same window do not re-query it.</summary>
        public bool ResultFetched;
        public ulong Frequency;
        public bool Disjoint;

        public D3D11TimestampDisjointWindow(ID3D11Query query) { Query = query; }

        public void ResetForReuse()
        {
            Ended = false;
            SlotReferences = 0;
            ResultFetched = false;
            Frequency = 0;
            Disjoint = false;
        }
    }

    /// <summary>
    ///     Direct3D11 <see cref="TimestampQueryPool"/>: one <c>ID3D11Query(D3D11_QUERY_TIMESTAMP)</c> per slot plus the same per-slot state machine
    ///     as the Vulkan pool, so the implicit-reset rule behaves identically on both backends (<c>_TASKS/GpuTimestampQueries.md</c> 5.3) even
    ///     though D3D11 needs no GPU-side reset. Shared between the recording thread and the resolving thread -> every transition takes
    ///     <see cref="StateLock"/>.
    /// </summary>
    internal sealed class D3D11TimestampQueryPool : TimestampQueryPool
    {
        /// <summary>Per-slot lifecycle; mirrors <c>VkTimestampQueryPool.SlotState</c>.</summary>
        internal enum SlotState : byte
        {
            Fresh = 0,
            Written = 1,
            Resolved = 2,
        }

        public override uint Capacity => capacity;
        public override ulong AbandonedWriteCount => abandonedWriteCount;
        public override bool IsDisposed => disposed;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                for (int i = 0; i < queries.Length; i++) queries[i].DebugName = value == null ? null : $"{value}[{i}]";
            }
        }

        internal readonly object StateLock = new object();

        /// <summary>The timestamp query behind each slot.</summary>
        internal readonly ID3D11Query[] Queries;

        /// <summary>The disjoint window each slot's current value belongs to; null for a Fresh slot. Guarded by <see cref="StateLock"/>.</summary>
        internal readonly D3D11TimestampDisjointWindow[] SlotWindows;

        private readonly D3D11GraphicsDevice gd;
        private readonly ID3D11Query[] queries;
        private readonly uint capacity;
        private readonly SlotState[] slotStates;
        private readonly bool[] slotFetched;    // true once the slot's current value was read by GetData; a non-fresh, unfetched slot is renamed on rewrite
        private static readonly QueryDescription timestampDescription = new QueryDescription(QueryType.Timestamp);
        private int resolvedSlotCount;          // how many slots are Resolved (fast "anything to advance?" poll at Begin, mirrors the Vulkan pool)
        private ulong abandonedWriteCount;
        private string name;
        private bool disposed;

        public D3D11TimestampQueryPool(D3D11GraphicsDevice gd, uint capacity)
        {
            this.gd = gd;
            this.capacity = capacity;
            slotStates = new SlotState[capacity];
            slotFetched = new bool[capacity];
            SlotWindows = new D3D11TimestampDisjointWindow[capacity];
            queries = new ID3D11Query[capacity];
            for (int i = 0; i < capacity; i++) queries[i] = gd.Device.CreateQuery(timestampDescription);
            Queries = queries;
            gd.RegisterTimestampPool(this);
        }

        /// <summary>True when at least one slot is Resolved; <see cref="D3D11CommandList.Begin"/> polls this before taking the lock.</summary>
        internal bool HasSlotsAwaitingReset => System.Threading.Volatile.Read(ref resolvedSlotCount) > 0;

        /// <summary>
        ///     The D3D11 half of <c>_TASKS/GpuTimestampQueries.md</c> 5.3 rule 2: D3D11 queries need no GPU reset, but the per-slot state machine
        ///     advances Resolved -> Fresh at the next <see cref="D3D11CommandList.Begin"/> exactly like the Vulkan pool, so a resolved slot becomes
        ///     writable inside a render pass on both backends at the same moment.
        /// </summary>
        internal void AdvanceResolvedSlotsToFresh()
        {
            lock (StateLock)
            {
                if (resolvedSlotCount == 0) return;
                for (uint i = 0; i < capacity; i++)
                {
                    if (slotStates[i] == SlotState.Resolved) slotStates[i] = SlotState.Fresh;
                }

                resolvedSlotCount = 0;
            }
        }

        /// <summary>
        ///     State transition for a write into <paramref name="index"/> under <paramref name="window"/>. Returns the previous state (the command
        ///     list uses it for the parity rule: a non-Fresh slot may only be rewritten outside an emulated render pass). Releases the previous
        ///     window reference (abandoned or resolved value) and takes one on the new window.
        /// </summary>
        internal SlotState MarkWritten(uint index, D3D11TimestampDisjointWindow window)
        {
            lock (StateLock)
            {
                SlotState previous = slotStates[index];
                if (previous == SlotState.Written) abandonedWriteCount++;
                else if (previous == SlotState.Resolved) resolvedSlotCount--;

                // Never re-issue an ID3D11Query whose previous result was not fetched with GetData (abandoned, or discarded by the automatic
                // layer). Observed Nov 10 2026 on NVIDIA RTX 4080 / 32.0.16.2101: two devices on two threads each building (CPU-side recording + FinishCommandList, before any submit) command lists that re-End() unfetched
                // timestamp queries across many pending command lists -> AccessViolation inside FinishCommandList; a single device, or
                // GetData between issues, never crashed (ContractTests TempParallelD3D11Probe bisect). Renaming keeps every query object on the
                // canonical issue -> GetData -> issue cycle; the pending command list holds its own COM reference on the old object.
                if (previous != SlotState.Fresh && !slotFetched[index])
                {
                    queries[index].Dispose();
                    queries[index] = gd.Device.CreateQuery(timestampDescription);
                    if (name != null) queries[index].DebugName = $"{name}[{index}]";
                }
                slotFetched[index] = false;

                releaseWindowLocked(index);
                slotStates[index] = SlotState.Written;
                SlotWindows[index] = window;
                window.SlotReferences++;
                return previous;
            }
        }

        /// <summary>Undo of <see cref="MarkWritten"/> for the skip path (write refused inside an emulated render pass in a non-validating build).</summary>
        internal void RestoreState(uint index, SlotState previous, D3D11TimestampDisjointWindow previousWindow)
        {
            lock (StateLock)
            {
                releaseWindowLocked(index);
                if (previous == SlotState.Resolved) resolvedSlotCount++;
                slotStates[index] = previous;
                SlotWindows[index] = previousWindow;
                if (previousWindow != null) previousWindow.SlotReferences++;
            }
        }

        /// <summary>Snapshot of a slot's state + window for the restore path. Caller does not need the lock for a single read pair that it will re-validate.</summary>
        internal void PeekLocked(uint index, out SlotState state, out D3D11TimestampDisjointWindow window)
        {
            state = slotStates[index];
            window = SlotWindows[index];
        }

        /// <summary>Marks <c>[first, first + count)</c> Resolved. The window references stay until the slot is rewritten (the value is still "owned" by that window). Caller holds <see cref="StateLock"/>.</summary>
        internal void MarkResolvedLocked(uint first, uint count)
        {
            markResolvedLockedCore(first, count, writtenOnly: false);
        }

        internal override void DiscardRange(uint first, uint count)
        {
            lock (StateLock) markResolvedLockedCore(first, count, writtenOnly: true);
        }

        private void markResolvedLockedCore(uint first, uint count, bool writtenOnly)
        {
            for (uint i = first; i < first + count; i++)
            {
                if (writtenOnly && slotStates[i] != SlotState.Written) continue;
                // Only a real read (writtenOnly == false) makes the query object safe to re-issue; a discard leaves it unfetched -> renamed on rewrite.
                slotFetched[i] = !writtenOnly;
                if (slotStates[i] != SlotState.Resolved)
                {
                    slotStates[i] = SlotState.Resolved;
                    resolvedSlotCount++;
                }
            }
        }

        internal bool AnyFreshLocked(uint first, uint count)
        {
            for (uint i = first; i < first + count; i++)
                if (slotStates[i] == SlotState.Fresh) return true;
            return false;
        }

        private void releaseWindowLocked(uint index)
        {
            var window = SlotWindows[index];
            if (window == null) return;
            SlotWindows[index] = null;
            window.SlotReferences--;
            if (window.SlotReferences == 0 && window.Ended) gd.ReturnTimestampDisjointWindow(window);
        }

        #region Disposal

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            gd.UnregisterTimestampPool(this);
            lock (StateLock)
            {
                for (uint i = 0; i < capacity; i++) releaseWindowLocked(i);
            }
            for (int i = 0; i < queries.Length; i++) queries[i].Dispose();
        }

        #endregion
    }
}