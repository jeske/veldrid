using System;
using Vulkan;
using static Vulkan.VulkanNative;

namespace Veldrid.Vk
{
    /// <summary>
    ///     Vulkan <see cref="TimestampQueryPool"/>: one <c>VkQueryPool(VK_QUERY_TYPE_TIMESTAMP)</c> plus the per-slot state machine that drives
    ///     the implicit reset rule (<c>_TASKS/GpuTimestampQueries.md</c> 5.3). Slot state is shared between the recording thread
    ///     (<see cref="VkCommandList.WriteTimestampCore"/>, <see cref="VkCommandList.Begin"/>) and whichever thread resolves
    ///     (<see cref="VkGraphicsDevice.TryResolveTimestampsCore"/>), so every transition takes <see cref="StateLock"/>.
    /// </summary>
    internal unsafe sealed class VkTimestampQueryPool : TimestampQueryPool
    {
        /// <summary>Per-slot lifecycle. <c>Fresh</c> = reset on the GPU and never written since; a write is legal anywhere.</summary>
        internal enum SlotState : byte
        {
            Fresh = 0,
            /// <summary>Written by a command list; the value is pending (not yet resolved). Rewriting abandons it.</summary>
            Written = 1,
            /// <summary>Resolved by the host; needs a <c>vkCmdResetQueryPool</c> before it can be written again (recorded lazily at the next Begin()).</summary>
            Resolved = 2,
        }

        public VkQueryPool DeviceQueryPool => queryPool;
        public override uint Capacity => capacity;
        public override ulong AbandonedWriteCount => abandonedWriteCount;
        public override bool IsDisposed => destroyed;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                gd.SetResourceName(this, value);
            }
        }

        /// <summary>Guards <see cref="slotStates"/>, <see cref="resolvedSlotCount"/> and <see cref="abandonedWriteCount"/>.</summary>
        internal readonly object StateLock = new object();

        /// <summary>Mask applied to every resolved value: the low <c>timestampValidBits</c> of the graphics queue family (B6).</summary>
        internal readonly ulong ValidBitsMask;

        private readonly VkGraphicsDevice gd;
        private readonly VkQueryPool queryPool;
        private readonly uint capacity;
        private readonly SlotState[] slotStates;
        private int resolvedSlotCount;          // how many slots are in Resolved state (fast "anything to reset?" check at Begin)
        private ulong abandonedWriteCount;
        private string name;
        private bool destroyed;
        private readonly bool registeredWithDevice;

        /// <param name="registerWithDevice">
        ///     True (app pools): joins <see cref="VkGraphicsDevice.TimestampPoolsSnapshot"/> so every <see cref="VkCommandList.Begin"/> records its pending resets (5.3 rule 2).
        ///     False (the automatic layer's per-recording trackers, <c>_TASKS/GpuTimestampQueries.md</c> section 10 F6): only the recording that owns the block records its
        ///     resets, into ITS OWN command buffer - a reset recorded by another list would make this list's write depend on that list submitting first.
        /// </param>
        public VkTimestampQueryPool(VkGraphicsDevice gd, uint capacity, uint timestampValidBits, bool registerWithDevice = true)
        {
            this.gd = gd;
            this.capacity = capacity;
            registeredWithDevice = registerWithDevice;
            slotStates = new SlotState[capacity];
            ValidBitsMask = timestampValidBits >= 64 ? ulong.MaxValue : (1UL << (int)timestampValidBits) - 1;

            var poolCi = VkQueryPoolCreateInfo.New();
            poolCi.queryType = VkQueryType.Timestamp;
            poolCi.queryCount = capacity;
            var result = vkCreateQueryPool(gd.Device, ref poolCi, null, out queryPool);
            VulkanUtil.CheckResult(result);

            // Vulkan requires every query to be reset before its first use; a freshly created pool is in an undefined state.
            // Mark everything Resolved so the next Begin() on any command list records the initial reset.
            for (int i = 0; i < slotStates.Length; i++) slotStates[i] = SlotState.Resolved;
            resolvedSlotCount = (int)capacity;

            if (registerWithDevice) gd.RegisterTimestampPool(this);
        }

        /// <summary>True when at least one slot needs a GPU-side reset; <see cref="VkCommandList.Begin"/> polls this before taking the lock.</summary>
        internal bool HasSlotsAwaitingReset => System.Threading.Volatile.Read(ref resolvedSlotCount) > 0;

        /// <summary>
        ///     Records <c>vkCmdResetQueryPool</c> for every <see cref="SlotState.Resolved"/> slot (coalesced into contiguous ranges) and marks them
        ///     <see cref="SlotState.Fresh"/>. Must be called OUTSIDE a render pass - <see cref="VkCommandList.Begin"/> is the one caller.
        /// </summary>
        internal void RecordPendingResets(VkCommandBuffer commandBuffer)
        {
            lock (StateLock)
            {
                if (resolvedSlotCount == 0) return;

                uint rangeStart = uint.MaxValue;
                for (uint i = 0; i <= capacity; i++)
                {
                    bool inRange = i < capacity && slotStates[i] == SlotState.Resolved;
                    if (inRange)
                    {
                        if (rangeStart == uint.MaxValue) rangeStart = i;
                        slotStates[i] = SlotState.Fresh;
                    }
                    else if (rangeStart != uint.MaxValue)
                    {
                        vkCmdResetQueryPool(commandBuffer, queryPool, rangeStart, i - rangeStart);
                        rangeStart = uint.MaxValue;
                    }
                }

                resolvedSlotCount = 0;
            }
        }

        /// <summary>
        ///     State transition for a write. Returns the state the slot was in BEFORE the write so the caller can decide whether a reset must be
        ///     recorded first (<see cref="SlotState.Written"/> / <see cref="SlotState.Resolved"/>) and whether that is legal where it stands.
        ///     Counts an abandoned value when the previous state was <see cref="SlotState.Written"/>. Marks the slot <see cref="SlotState.Written"/>.
        /// </summary>
        internal SlotState MarkWritten(uint index)
        {
            lock (StateLock)
            {
                SlotState previous = slotStates[index];
                if (previous == SlotState.Written) abandonedWriteCount++;
                else if (previous == SlotState.Resolved) resolvedSlotCount--;
                slotStates[index] = SlotState.Written;
                return previous;
            }
        }

        /// <summary>Undo of <see cref="MarkWritten"/> for the skip path (write refused inside a render pass in a non-validating build).</summary>
        internal void RestoreState(uint index, SlotState previous)
        {
            lock (StateLock)
            {
                if (previous == SlotState.Resolved) resolvedSlotCount++;
                slotStates[index] = previous;
            }
        }

        /// <summary>Marks <c>[first, first + count)</c> <see cref="SlotState.Resolved"/> after a successful read. Caller holds <see cref="StateLock"/>.</summary>
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
                if (slotStates[i] != SlotState.Resolved)
                {
                    slotStates[i] = SlotState.Resolved;
                    resolvedSlotCount++;
                }
            }
        }

        /// <summary>True when any slot in the range is <see cref="SlotState.Fresh"/> (never written since its reset) - the result cannot be available. Caller holds <see cref="StateLock"/>.</summary>
        internal bool AnyFreshLocked(uint first, uint count)
        {
            for (uint i = first; i < first + count; i++)
                if (slotStates[i] == SlotState.Fresh) return true;
            return false;
        }

        #region Disposal

        public override void Dispose()
        {
            if (!destroyed)
            {
                destroyed = true;
                if (registeredWithDevice) gd.UnregisterTimestampPool(this);
                vkDestroyQueryPool(gd.Device, queryPool, null);
            }
        }

        #endregion
    }
}