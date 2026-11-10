using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Veldrid
{
    /// <summary>What the automatic GPU timing layer brackets. Flags; <see cref="Off"/> costs one bool check per hook.</summary>
    [Flags]
    public enum GpuTiming_Mode
    {
        /// <summary>Nothing is recorded or resolved.</summary>
        Off = 0,

        /// <summary>Every <see cref="CommandList"/> recording gets a start timestamp at <see cref="CommandList.Begin"/> and an end timestamp at <see cref="CommandList.End"/>.</summary>
        CommandLists = 1,

        /// <summary>Every <see cref="CommandList.PushDebugGroup"/> / <see cref="CommandList.PopDebugGroup"/> pair is timed as a nested span inside its command list.</summary>
        DebugGroups = 2,
    }

    /// <summary>Monotonic per-device index of a <see cref="GraphicsDevice.SubmitCommands(CommandList)"/> call (only submits made while timing was on are numbered).</summary>
    public readonly record struct GpuTiming_SubmitIndex(ulong Value);

    /// <summary>
    ///     One completed command-list span. <see cref="GpuStartTicks"/> / <see cref="GpuEndTicks"/> are device-absolute GPU ticks (end-of-pipe,
    ///     masked to the valid bit width); <c>(end - start) / TicksPerSecond</c> is the GPU duration in seconds. <see cref="CpuSubmitTimestamp"/> is
    ///     <see cref="Stopwatch.GetTimestamp"/> taken inside <c>SubmitCommands</c> - the pragmatic CPU anchor (GPU start is never before it).
    /// </summary>
    public readonly record struct GpuTiming_CommandListSpan(
        GpuTiming_SubmitIndex Submit,
        string CommandListName,
        ulong GpuStartTicks,
        ulong GpuEndTicks,
        double TicksPerSecond,
        long CpuSubmitTimestamp);

    /// <summary>
    ///     One completed debug-group span. <see cref="ParentSubmit"/> links it to its <see cref="GpuTiming_CommandListSpan"/>; <see cref="Depth"/> is 0
    ///     for an outermost group in that command list. A group still open at <c>End()</c> is closed there and reported with the name suffixed
    ///     <c>" (unclosed)"</c>.
    /// </summary>
    public readonly record struct GpuTiming_DebugGroupSpan(
        GpuTiming_SubmitIndex ParentSubmit,
        string GroupName,
        int Depth,
        ulong GpuStartTicks,
        ulong GpuEndTicks,
        double TicksPerSecond);

    /// <summary>
    ///     Device-owned automatic GPU timing (<c>_TASKS/GpuTimestampQueries.md</c> section 4). When <see cref="Mode"/> is on, the fork brackets every
    ///     submitted <see cref="CommandList"/> (and optionally every debug group) with timestamps from an internal ring pool - the app marks
    ///     nothing. Results are pulled with <see cref="DrainCompleted"/>, which never blocks; spans whose GPU values are not in yet stay queued.
    ///     Nothing here ever waits for the GPU. When the ring is full a command list is recorded WITHOUT timestamps and
    ///     <see cref="DroppedCommandLists"/> is incremented.
    ///     <para>
    ///         Threading: recording hooks run on the recording threads (several command lists may record concurrently), <c>SubmitCommands</c> and
    ///         <see cref="DrainCompleted"/> on whichever threads the app uses; the ring and the pending queue are guarded by one lock, while
    ///         per-command-list state lives on the command list itself and needs none.
    ///     </para>
    /// </summary>
    public sealed class GpuTiming
    {
        /// <summary><see cref="GraphicsDeviceFeatures.TimestampQueries"/>. When false every hook is a no-op and <see cref="DrainCompleted"/> returns nothing.</summary>
        public bool Supported => device.Features.TimestampQueries;

        /// <summary>
        ///     Runtime toggle. Takes effect at the next <see cref="CommandList.Begin"/>; a command list already recording keeps the mode it started
        ///     with. Setting <see cref="GpuTiming_Mode.Off"/> releases nothing - the ring pool stays allocated once created (re-enable is free).
        /// </summary>
        public GpuTiming_Mode Mode
        {
            get => mode;
            set
            {
                var effective = Supported ? value : GpuTiming_Mode.Off;
                if (effective != GpuTiming_Mode.Off)
                {
                    // Create the ring HERE, not at the first Begin(): CommandList.Begin() records the lazy resets of resolved slots BEFORE the
                    // timing hook runs, so a pool born inside that hook would miss its initial reset (Vulkan pools start undefined = Resolved).
                    lock (ringLock) { if (ring == null) createRingLocked(); }
                }

                mode = effective;
            }
        }

        /// <summary>
        ///     Capacity of the internal ring in (start, end) timestamp pairs. Every traced command list reserves
        ///     <c>1 + <see cref="MaxDebugGroupsPerCommandList"/></c> pairs at <c>Begin()</c>, so the default (2048 pairs, 16 groups) keeps ~120 command
        ///     lists in flight. Applied when the ring is (re)created: the first time <see cref="Mode"/> turns on, or after
        ///     <see cref="RecreateRing"/>.
        /// </summary>
        public uint RingSlotPairs { get; set; } = 2048;

        /// <summary>
        ///     Debug-group pairs reserved per command list (nesting or sequence; whichever, this many pushes are timed, further pushes are still
        ///     forwarded to the graphics debugger but not timed and counted in <see cref="UntimedDebugGroups"/>). Applied with <see cref="RingSlotPairs"/>.
        /// </summary>
        public uint MaxDebugGroupsPerCommandList { get; set; } = 16;

        /// <summary>Command lists recorded without timestamps because the ring was full. Never blocks; grows instead.</summary>
        public ulong DroppedCommandLists => droppedCommandLists;

        /// <summary>Command-list spans discarded because Direct3D11 reported a GPU clock change (disjoint) across them.</summary>
        public ulong DisjointCommandLists => disjointCommandLists;

        /// <summary>Debug-group pushes that exceeded <see cref="MaxDebugGroupsPerCommandList"/> for their command list and were therefore not timed.</summary>
        public ulong UntimedDebugGroups => untimedDebugGroups;

        /// <summary>Command lists that were Begin()'d while timing was on but never submitted (their reserved slots are recycled unread).</summary>
        public ulong AbandonedCommandLists => abandonedCommandLists;

        /// <summary>
        ///     Moves every span whose GPU values are available into the lists (appends; the caller clears). Non-blocking: stops at the first
        ///     command list, in submit order, whose values are not in yet. Call once per frame.
        /// </summary>
        public void DrainCompleted(List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups)
        {
            if (commandLists == null) throw new ArgumentNullException(nameof(commandLists));
            if (debugGroups == null) throw new ArgumentNullException(nameof(debugGroups));

            lock (ringLock)
            {
                if (ring == null) return;

                while (pending.Count > 0)
                {
                    var record = pending.Peek();

                    if (record.Abandoned)
                    {
                        // Begin()'d, never submitted: the GPU never ran it. Mark its written slots consumed so the next Begin() resets them.
                        ring.DiscardRange(record.FirstSlot, record.UsedSlots);
                    }
                    else if (record.WrittenSlotCount > 0)
                    {
                        // Resolve only the WRITTEN prefix: in DebugGroups-only mode the reserved command-list pair stays Fresh and would
                        // otherwise keep the range NotReady forever.
                        uint resolveFirst = record.ResolveFirstSlot;
                        uint resolveCount = record.WrittenSlotCount;
                        Span<ulong> ticks = resolveCount <= 64 ? stackalloc ulong[(int)resolveCount] : new ulong[resolveCount];
                        var result = device.TryResolveTimestamps(ring, resolveFirst, resolveCount, ticks, out double ticksPerSecond);
                        if (result == TimestampResolveResult.NotReady) return;   // submit order == resolve order; later ones cannot be ready either

                        if (result == TimestampResolveResult.Disjoint)
                        {
                            disjointCommandLists++;
                        }
                        else
                        {
                            emitSpans(record, ticks, ticksPerSecond, commandLists, debugGroups);
                        }
                    }

                    pending.Dequeue();
                    usedRingSlots -= record.ReservedSlots + record.WasteSlots;
                    recordPool.Push(record.Clear());
                }
            }
        }

        /// <summary>
        ///     Disposes the ring pool (after the caller has drained or given up on pending spans) so the next enable builds it from the current
        ///     <see cref="RingSlotPairs"/> / <see cref="MaxDebugGroupsPerCommandList"/>. Must not be called while any command list is recording with
        ///     timing on; <see cref="Mode"/> is forced <see cref="GpuTiming_Mode.Off"/>.
        /// </summary>
        public void RecreateRing()
        {
            lock (ringLock)
            {
                mode = GpuTiming_Mode.Off;
                pending.Clear();
                usedRingSlots = 0;
                ringAllocHead = 0;
                ring?.Dispose();
                ring = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------------------------------------------------------------

        private readonly GraphicsDevice device;
        private volatile GpuTiming_Mode mode;

        private readonly object ringLock = new object();
        private TimestampQueryPool ring;                     // created on first enable; 2 * RingSlotPairs slots
        private uint ringCapacitySlots;
        private uint ringAllocHead;                          // next free slot (ring order)
        private uint usedRingSlots;                          // reserved + wasted slots of every pending record
        private uint slotsPerCommandList;                    // 2 + 2 * MaxDebugGroupsPerCommandList, frozen at ring creation
        private readonly Queue<GpuTiming_CommandListRecord> pending = new Queue<GpuTiming_CommandListRecord>();
        private readonly Stack<GpuTiming_CommandListRecord> recordPool = new Stack<GpuTiming_CommandListRecord>();
        private ulong nextSubmitIndex;

        private ulong droppedCommandLists;
        private ulong disjointCommandLists;
        private ulong untimedDebugGroups;
        private ulong abandonedCommandLists;

        internal GpuTiming(GraphicsDevice device)
        {
            this.device = device;
        }

        /// <summary>Called from <see cref="GraphicsDevice.Dispose"/> before the backend tears down.</summary>
        internal void DisposeRing()
        {
            RecreateRing();
        }

        /// <summary>
        ///     <see cref="CommandList.Begin"/> hook, AFTER the backend's BeginCore (so the command buffer exists and pending resets are recorded).
        ///     Reserves this recording's slot block and writes the start timestamp. Returns null when timing is off / unsupported / ring full.
        /// </summary>
        internal GpuTiming_CommandListRecord OnCommandListBegin(CommandList commandList, GpuTiming_CommandListRecord previousRecord)
        {
            if (previousRecord != null) abandon(previousRecord);   // Begin() again without a submit

            var activeMode = mode;
            if (activeMode == GpuTiming_Mode.Off) return null;

            GpuTiming_CommandListRecord record;
            lock (ringLock)
            {
                if (ring == null) createRingLocked();

                // Contiguous block; if it does not fit before the end of the ring, the tail is wasted and accounted to this record.
                uint waste = 0;
                if (ringAllocHead + slotsPerCommandList > ringCapacitySlots)
                {
                    waste = ringCapacitySlots - ringAllocHead;
                    if (usedRingSlots + waste + slotsPerCommandList > ringCapacitySlots) { droppedCommandLists++; return null; }
                    ringAllocHead = 0;
                }
                else if (usedRingSlots + slotsPerCommandList > ringCapacitySlots) { droppedCommandLists++; return null; }

                record = recordPool.Count > 0 ? recordPool.Pop() : new GpuTiming_CommandListRecord();
                record.Mode = activeMode;
                record.FirstSlot = ringAllocHead;
                record.ReservedSlots = slotsPerCommandList;
                record.WasteSlots = waste;
                record.MaxGroups = (slotsPerCommandList - 2) / 2;
                ringAllocHead += slotsPerCommandList;
                usedRingSlots += slotsPerCommandList + waste;
            }

            // Slot layout: [0]=cl start, [1]=cl end, then (start,end) per debug group in push order.
            if ((activeMode & GpuTiming_Mode.CommandLists) != 0)
            {
                commandList.WriteTimestamp(ring, record.FirstSlot);
                record.CommandListTimed = true;
            }
            record.UsedSlots = 2;   // the pair is reserved even when only groups are timed, keeps the layout fixed
            return record;
        }

        /// <summary><see cref="CommandList.PushDebugGroup"/> hook (runs in addition to the backend marker).</summary>
        internal void OnPushDebugGroup(CommandList commandList, GpuTiming_CommandListRecord record, string name)
        {
            if ((record.Mode & GpuTiming_Mode.DebugGroups) == 0) return;

            if (record.Groups.Count >= record.MaxGroups)
            {
                untimedDebugGroups++;
                record.OpenGroups.Push(-1);   // keeps push/pop pairing right; -1 = untimed
                return;
            }

            uint startSlot = record.FirstSlot + 2 + 2 * (uint)record.Groups.Count;
            record.Groups.Add(new GpuTiming_DebugGroupEntry(name, record.OpenGroups.Count - countUntimed(record.OpenGroups), startSlot));
            record.OpenGroups.Push(record.Groups.Count - 1);
            record.UsedSlots = Math.Max(record.UsedSlots, 2 + 2 * (uint)record.Groups.Count);
            commandList.WriteTimestamp(ring, startSlot);
        }

        /// <summary><see cref="CommandList.PopDebugGroup"/> hook.</summary>
        internal void OnPopDebugGroup(CommandList commandList, GpuTiming_CommandListRecord record)
        {
            if ((record.Mode & GpuTiming_Mode.DebugGroups) == 0 || record.OpenGroups.Count == 0) return;

            int groupIndex = record.OpenGroups.Pop();
            if (groupIndex < 0) return;   // untimed (over the per-list cap)

            var entry = record.Groups[groupIndex];
            commandList.WriteTimestamp(ring, entry.StartSlot + 1);
            record.Groups[groupIndex] = entry with { Closed = true };
        }

        /// <summary><see cref="CommandList.End"/> hook, BEFORE the backend's EndCore (the command buffer is still open; a render pass may be active - a Fresh slot write is legal there).</summary>
        internal void OnCommandListEnd(CommandList commandList, GpuTiming_CommandListRecord record)
        {
            // Close stragglers so every timed group has an end value.
            while (record.OpenGroups.Count > 0)
            {
                int groupIndex = record.OpenGroups.Pop();
                if (groupIndex < 0) continue;
                var entry = record.Groups[groupIndex];
                commandList.WriteTimestamp(ring, entry.StartSlot + 1);
                record.Groups[groupIndex] = entry with { Closed = true, Name = entry.Name + " (unclosed)" };
            }

            if (record.CommandListTimed) commandList.WriteTimestamp(ring, record.FirstSlot + 1);
            record.Ended = true;
        }

        /// <summary><see cref="GraphicsDevice.SubmitCommands(CommandList)"/> hook, after the backend submitted. Numbers the submit and queues the record for resolve.</summary>
        internal void OnCommandListSubmitted(CommandList commandList, GpuTiming_CommandListRecord record)
        {
            record.CommandListName = commandList.Name;
            record.CpuSubmitTimestamp = Stopwatch.GetTimestamp();
            lock (ringLock)
            {
                record.Submit = new GpuTiming_SubmitIndex(nextSubmitIndex++);
                pending.Enqueue(record);
            }
        }

        private void abandon(GpuTiming_CommandListRecord record)
        {
            abandonedCommandLists++;
            record.Abandoned = true;
            lock (ringLock) pending.Enqueue(record);   // freed in ring order when it reaches the head of the queue
        }

        private void createRingLocked()
        {
            slotsPerCommandList = 2 + 2 * Math.Max(0u, MaxDebugGroupsPerCommandList);
            ringCapacitySlots = Math.Max(slotsPerCommandList, 2 * Math.Max(1u, RingSlotPairs));
            ring = device.ResourceFactory.CreateTimestampQueryPool(ringCapacitySlots);
            ring.Name = "Veldrid.GpuTiming.Ring";
            ringAllocHead = 0;
            usedRingSlots = 0;
        }

        private static void emitSpans(GpuTiming_CommandListRecord record, ReadOnlySpan<ulong> ticks, double ticksPerSecond,
            List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups)
        {
            // ticks[0] corresponds to record.ResolveFirstSlot (see DrainCompleted), not necessarily to record.FirstSlot.
            uint resolveFirst = record.ResolveFirstSlot;
            if (record.CommandListTimed)
                commandLists.Add(new GpuTiming_CommandListSpan(record.Submit, record.CommandListName, ticks[0], ticks[1], ticksPerSecond, record.CpuSubmitTimestamp));

            for (int i = 0; i < record.Groups.Count; i++)
            {
                var entry = record.Groups[i];
                if (!entry.Closed) continue;
                int offset = (int)(entry.StartSlot - resolveFirst);
                debugGroups.Add(new GpuTiming_DebugGroupSpan(record.Submit, entry.Name, entry.Depth, ticks[offset], ticks[offset + 1], ticksPerSecond));
            }
        }

        private static int countUntimed(Stack<int> openGroups)
        {
            int n = 0;
            foreach (int g in openGroups) if (g < 0) n++;
            return n;
        }
    }

    /// <summary>One timed debug group inside a <see cref="GpuTiming_CommandListRecord"/>; <c>EndSlot = StartSlot + 1</c>.</summary>
    internal readonly record struct GpuTiming_DebugGroupEntry(string Name, int Depth, uint StartSlot, bool Closed = false);

    /// <summary>Per-recording state of a traced <see cref="CommandList"/>; owned by the command list between Begin() and the submit that queues it.</summary>
    internal sealed class GpuTiming_CommandListRecord
    {
        public GpuTiming_Mode Mode;
        public uint FirstSlot;
        public uint ReservedSlots;
        public uint WasteSlots;
        public uint UsedSlots;          // 2 + 2 * timed groups; the prefix of the reserved block that was actually written
        public uint MaxGroups;
        public bool CommandListTimed;
        public bool Ended;
        public bool Abandoned;

        /// <summary>First slot that was actually written: the reserved command-list pair is skipped when only debug groups are timed.</summary>
        public uint ResolveFirstSlot => CommandListTimed ? FirstSlot : FirstSlot + 2;

        /// <summary>Number of written slots from <see cref="ResolveFirstSlot"/>; 0 when nothing was timed in this recording.</summary>
        public uint WrittenSlotCount => UsedSlots - (ResolveFirstSlot - FirstSlot);
        public string CommandListName;
        public long CpuSubmitTimestamp;
        public GpuTiming_SubmitIndex Submit;
        public readonly List<GpuTiming_DebugGroupEntry> Groups = new List<GpuTiming_DebugGroupEntry>();
        public readonly Stack<int> OpenGroups = new Stack<int>();   // indices into Groups; -1 = pushed past the cap (untimed)

        public GpuTiming_CommandListRecord Clear()
        {
            Mode = GpuTiming_Mode.Off;
            FirstSlot = ReservedSlots = WasteSlots = UsedSlots = MaxGroups = 0;
            CommandListTimed = Ended = Abandoned = false;
            CommandListName = null;
            CpuSubmitTimestamp = 0;
            Submit = default;
            Groups.Clear();
            OpenGroups.Clear();
            return this;
        }
    }
}