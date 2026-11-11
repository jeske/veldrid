using System;
using System.Collections.Concurrent;
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
    ///     Device-owned automatic GPU timing (<c>_TASKS/GpuTimestampQueries.md</c> section 4; allocator revised in section 10). When <see cref="Mode"/> is on,
    ///     the fork brackets every submitted <see cref="CommandList"/> (and optionally every debug group) with timestamps - the app marks nothing. Results are
    ///     pulled with <see cref="DrainCompleted"/>, which never blocks; spans whose GPU values are not in yet stay queued. Nothing here ever waits for the GPU.
    ///     <para>
    ///         STORAGE (section 10, F1-F5): every recording owns ONE <see cref="GpuTiming_SlotBlock"/> - a self-contained <see cref="TimestampQueryPool"/> of
    ///         <c>2 + 2 x MaxDebugGroupsPerCommandList</c> slots, its own device object (D3D11: its queries + disjoint window; Vulkan: its own small VkQueryPool).
    ///         <see cref="freeBlocks"/> is a CACHE of trackers, not an owner: <c>Begin()</c> takes any free tracker (creating one up to <see cref="MaxTrackedCommandLists"/>,
    ///         else the recording is traced without timestamps and <see cref="DroppedCommandLists"/> grows), resolve or abandon returns that tracker. Veldrid places
    ///         NO ordering constraint on command lists - any thread, any Begin / Submit interleaving, held for as long as the caller likes - and this POOLED allocator
    ///         depends on none: the tracker rides on the recording, nothing is addressed by position or count. (Its predecessor, a single shared pool allocated by
    ///         position at Begin and freed by count in submit order, assumed Begin order == Submit order and reissued a still-Written slot after any GPU idle -
    ///         AN_Monsters, Nov 10 2026.)
    ///     </para>
    ///     <para>
    ///         FAIL GRACEFULLY: this is a performance debugger. It never blocks, and it never throws into the client for a device reason: a full pool drops, a dead
    ///         head of the resolve queue times out (<see cref="PendingResolveTimeout"/>), and any exception from its own device calls disables it with the message in
    ///         <see cref="Fault"/>. The client's rendering is unaffected in every case; only the tracing stops.
    ///     </para>
    ///     <para>
    ///         LOST LISTS: a tracker is reachable only through its <see cref="CommandList"/> (and <see cref="pending"/> while submitted). A list dropped on the
    ///         floor - never submitted, never re-Begin()'d - takes its tracker with it; the tracker's finalizer reports to THIS object and nothing else
    ///         (<see cref="ReportFinalized"/>: a queue push - no driver call ever runs on the finalizer thread). The next <c>Begin()</c> / <c>DrainCompleted()</c> drains
    ///         that queue under the lock and re-pools the tracker (its queries are intact; nothing GPU-side can reference a collectable tracker) or, over the cap,
    ///         disposes it on that app thread. The block's pool is NOT in the device's implicit-reset registry: only the recording that owns it resets it, into its
    ///         own command buffer (<see cref="CommandList.GpuTiming_ResetBlockSlots"/>), so no write ever depends on another list's submit order (F6).
    ///     </para>
    ///     <para>
    ///         Threading: recording hooks run on the recording threads (several command lists may record concurrently), <c>SubmitCommands</c> and
    ///         <see cref="DrainCompleted"/> on whichever threads the app uses; <see cref="freeBlocks"/>, <see cref="liveBlocks"/> and the pending queue are guarded
    ///         by one lock, <see cref="finalizedBlocks"/> is lock-free and drained only under that lock, while per-command-list state lives on the command list
    ///         itself and per-slot state on the block's pool (<c>StateLock</c>).
    ///     </para>
    /// </summary>
    public sealed class GpuTiming
    {
        /// <summary><see cref="GraphicsDeviceFeatures.TimestampQueries"/>. When false every hook is a no-op and <see cref="DrainCompleted"/> returns nothing.</summary>
        public bool Supported => device.Features.TimestampQueries;

        /// <summary>
        ///     Runtime toggle. Takes effect at the next <see cref="CommandList.Begin"/>; a command list already recording keeps the mode it started
        ///     with. Setting <see cref="GpuTiming_Mode.Off"/> releases nothing - pooled trackers stay allocated (re-enable is free). While <see cref="Fault"/>
        ///     is set the layer stays Off: call <see cref="ResetTrackerPool"/> first.
        /// </summary>
        public GpuTiming_Mode Mode
        {
            get => mode;
            set
            {
                var effective = Supported && fault == null ? value : GpuTiming_Mode.Off;
                lock (poolLock)
                {
                    // Freeze the tracker geometry HERE: MaxTrackedCommandLists / MaxDebugGroupsPerCommandList apply until ResetTrackerPool.
                    if (effective != GpuTiming_Mode.Off && slotsPerCommandList == 0) configureTrackerPoolLocked();
                    mode = effective;
                }
            }
        }

        /// <summary>
        ///     Cap on trackers alive at once = recordings that can be in flight (recording, submitted-and-unresolved, or held) with timestamps. Each tracker is
        ///     <c>2 + 2 x <see cref="MaxDebugGroupsPerCommandList"/></c> GPU query slots; the default 120 x 34 slots matches the old 2048-pair budget. A recording
        ///     that starts while every tracker is in flight is traced without timestamps (<see cref="DroppedCommandLists"/>) - never blocks. Applied when the
        ///     geometry is (re)frozen: the first time <see cref="Mode"/> turns on, or after <see cref="ResetTrackerPool"/>.
        /// </summary>
        public int MaxTrackedCommandLists { get; set; } = 120;

        /// <summary>
        ///     Debug-group pairs reserved per recording (nesting or sequence; whichever, this many pushes are timed, further pushes are still
        ///     forwarded to the graphics debugger but not timed and counted in <see cref="UntimedDebugGroups"/>). Applied with <see cref="MaxTrackedCommandLists"/>.
        /// </summary>
        public uint MaxDebugGroupsPerCommandList { get; set; } = 16;

        /// <summary>
        ///     How long a submitted recording may sit at the head of the resolve queue with its GPU values not in before it is written off as lost
        ///     (<see cref="TimedOutCommandLists"/>) and its tracker reclaimed. Protects the pool from a device that stopped answering (device lost, a never-
        ///     signalled submit): without it every tracker would end up queued behind the dead head and the layer would drop everything forever. Default 10 s -
        ///     far longer than any legitimate GPU latency; a GPU that is merely busy keeps resolving in order and never trips it.
        /// </summary>
        public TimeSpan PendingResolveTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Recordings traced without timestamps because every tracker up to the cap was in flight. Never blocks; grows instead.</summary>
        public ulong DroppedCommandLists => droppedCommandLists;

        /// <summary>Command-list spans discarded because Direct3D11 reported a GPU clock change (disjoint) across them.</summary>
        public ulong DisjointCommandLists => disjointCommandLists;

        /// <summary>Debug-group pushes that exceeded <see cref="MaxDebugGroupsPerCommandList"/> for their command list and were therefore not timed.</summary>
        public ulong UntimedDebugGroups => untimedDebugGroups;

        /// <summary>Command lists that were Begin()'d while timing was on but never submitted (their block is recycled unread at the next Begin() on that list).</summary>
        public ulong AbandonedCommandLists => abandonedCommandLists;

        /// <summary>Submitted recordings whose GPU values never arrived within <see cref="PendingResolveTimeout"/>; their spans are lost and their trackers reclaimed. Non-zero means the device stopped completing work.</summary>
        public ulong TimedOutCommandLists => timedOutCommandLists;

        /// <summary>Trackers whose command list was garbage-collected with a live recording (section 10 F5): reported by the finalizer, re-pooled or disposed at the next drain. A growing count means a client drops command lists without disposing them.</summary>
        public ulong FinalizedBlocks => finalizedBlocksTotal;

        /// <summary>Trackers of the current pool generation currently alive (pooled + in recordings + pending); never exceeds <see cref="MaxTrackedCommandLists"/>.</summary>
        public int LiveBlocks { get { lock (poolLock) return liveBlocks; } }

        /// <summary>
        ///     Null while healthy. Set (and <see cref="Mode"/> forced <see cref="GpuTiming_Mode.Off"/>) the first time one of the layer's OWN device calls throws -
        ///     this is a profiler, it must never take the client down: the recording that hit the error continues untimed, nothing propagates. The message names the
        ///     operation and the exception. Clear it with <see cref="ResetTrackerPool"/>; <see cref="FaultCount"/> keeps counting across resets.
        /// </summary>
        public string Fault => fault;

        /// <summary>Number of times the layer disabled itself (see <see cref="Fault"/>).</summary>
        public ulong FaultCount => faults;

        /// <summary>
        ///     Moves every span whose GPU values are available into the lists (appends; the caller clears). Non-blocking: stops at the first
        ///     command list, in submit order, whose values are not in yet (a later submit cannot have results before an earlier one) - unless that head has
        ///     waited longer than <see cref="PendingResolveTimeout"/>, in which case it is written off and the drain continues. Never throws for a device reason
        ///     (see <see cref="Fault"/>). Call once per frame.
        /// </summary>
        public void DrainCompleted(List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups)
        {
            if (commandLists == null) throw new ArgumentNullException(nameof(commandLists));
            if (debugGroups == null) throw new ArgumentNullException(nameof(debugGroups));

            lock (poolLock)
            {
                if (slotsPerCommandList == 0) return;
                drainFinalizedLocked();

                // One scratch range for the whole drain: a tracker is at most slotsPerCommandList slots (CA2014: no stackalloc inside the loop).
                Span<ulong> scratch = slotsPerCommandList <= 64 ? stackalloc ulong[(int)slotsPerCommandList] : new ulong[slotsPerCommandList];
                long now = Stopwatch.GetTimestamp();
                long timeoutTicks = (long)(PendingResolveTimeout.TotalSeconds * Stopwatch.Frequency);

                while (pending.Count > 0)
                {
                    var record = pending.Peek();

                    if (record.Abandoned || record.Faulted)
                    {
                        // Begin()'d and never submitted (the GPU never ran it), or a write threw midway: mark its written slots consumed so the next owner resets them.
                        record.Block.Pool.DiscardRange(0, record.UsedSlots);
                    }
                    else if (record.WrittenSlotCount > 0)
                    {
                        // Resolve only the WRITTEN prefix: in DebugGroups-only mode the reserved command-list pair stays Fresh and would
                        // otherwise keep the range NotReady forever.
                        uint resolveFirst = record.ResolveFirstSlot;
                        uint resolveCount = record.WrittenSlotCount;
                        Span<ulong> ticks = scratch.Slice(0, (int)resolveCount);
                        TimestampResolveResult result;
                        double ticksPerSecond;
                        try { result = device.TryResolveTimestamps(record.Block.Pool, resolveFirst, resolveCount, ticks, out ticksPerSecond); }
                        catch (Exception e) { faultLocked("resolving timestamps", e); result = TimestampResolveResult.Disjoint; ticksPerSecond = 0; }

                        if (result == TimestampResolveResult.NotReady)
                        {
                            // Submit order == resolve order: later ones cannot be ready either... unless this head is dead (device stopped completing work).
                            if (now - record.CpuSubmitTimestamp < timeoutTicks) return;
                            timedOutCommandLists++;
                            record.Block.Pool.DiscardRange(0, record.UsedSlots);
                        }
                        else if (result == TimestampResolveResult.Disjoint)
                        {
                            disjointCommandLists++;
                        }
                        else
                        {
                            emitSpans(record, ticks, ticksPerSecond, commandLists, debugGroups);
                        }
                    }

                    pending.Dequeue();
                    returnBlockLocked(record.Block);
                    recordPool.Push(record.Clear());
                }
            }
        }

        /// <summary>
        ///     Closes the current pool generation: disposes every cached tracker, drops pending spans, clears <see cref="Fault"/>, and forces <see cref="Mode"/>
        ///     <see cref="GpuTiming_Mode.Off"/> so the next enable rebuilds the cache from the current <see cref="MaxTrackedCommandLists"/> /
        ///     <see cref="MaxDebugGroupsPerCommandList"/>. Trackers still held by live recordings belong to the closed generation: when they come back (submit,
        ///     abandon, or GC -> <see cref="ReportFinalized"/>) they are disposed on that app thread, uncounted. Must not be called while a command list is
        ///     recording with timing on.
        /// </summary>
        public void ResetTrackerPool()
        {
            lock (poolLock)
            {
                mode = GpuTiming_Mode.Off;
                while (pending.Count > 0)
                {
                    var record = pending.Dequeue();
                    disposeBlockLocked(record.Block);
                    recordPool.Push(record.Clear());
                }
                drainFinalizedLocked(disposeAll: true);
                while (freeBlocks.Count > 0) disposeBlockLocked(freeBlocks.Pop());
                slotsPerCommandList = 0;
                blockCap = 0;
                liveBlocks = 0;   // trackers still owned by live recordings are of the closed generation: disposed, uncounted, when they return
                fault = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------------------------------------------------------------

        private readonly GraphicsDevice device;
        private volatile GpuTiming_Mode mode;

        /// <summary>Guards <see cref="freeBlocks"/>, <see cref="liveBlocks"/>, <see cref="pending"/>, <see cref="recordPool"/>, <see cref="fault"/> and the frozen geometry.</summary>
        private readonly object poolLock = new object();
        private readonly Stack<GpuTiming_SlotBlock> freeBlocks = new Stack<GpuTiming_SlotBlock>();
        private int liveBlocks;                              // current-generation trackers created and not yet disposed, including those owned by recordings
        private int blockCap;                                // max liveBlocks = MaxTrackedCommandLists, frozen with the geometry
        private int poolGeneration;                          // bumped by configureTrackerPoolLocked; trackers carry the generation they were created in
        private uint slotsPerCommandList;                    // 2 + 2 * MaxDebugGroupsPerCommandList, frozen at first enable; 0 = not configured
        private readonly ConcurrentQueue<GpuTiming_SlotBlock> finalizedBlocks = new ConcurrentQueue<GpuTiming_SlotBlock>();   // written by finalizers, drained under poolLock
        private readonly Queue<GpuTiming_CommandListRecord> pending = new Queue<GpuTiming_CommandListRecord>();
        private readonly Stack<GpuTiming_CommandListRecord> recordPool = new Stack<GpuTiming_CommandListRecord>();
        private ulong nextSubmitIndex;

        private ulong droppedCommandLists;
        private ulong disjointCommandLists;
        private ulong untimedDebugGroups;
        private ulong abandonedCommandLists;
        private ulong timedOutCommandLists;
        private ulong finalizedBlocksTotal;
        private volatile string fault;
        private ulong faults;

        internal GpuTiming(GraphicsDevice device)
        {
            this.device = device;
        }

        /// <summary>Called from <see cref="GraphicsDevice.Dispose"/> before the backend tears down.</summary>
        internal void DisposeTrackerPool()
        {
            ResetTrackerPool();
        }

        /// <summary>
        ///     The ONLY thing a tracker's finalizer does (section 10 F5): hand itself back. Any thread, no locks, no device access - the tracker is
        ///     resurrected onto a lock-free queue that the next <see cref="OnCommandListBegin"/> / <see cref="DrainCompleted"/> drains under <see cref="poolLock"/>.
        /// </summary>
        internal void ReportFinalized(GpuTiming_SlotBlock block)
        {
            finalizedBlocks.Enqueue(block);
        }

        /// <summary>
        ///     <see cref="CommandList.Begin"/> hook, AFTER the backend's BeginCore (so the command buffer exists). Takes a tracker, resets ITS slots into this
        ///     command buffer and writes the start timestamp. Returns null when timing is off / unsupported / every tracker up to the cap is in flight / the
        ///     layer faulted (see <see cref="Fault"/>) - the recording then proceeds untimed, never fails.
        /// </summary>
        internal GpuTiming_CommandListRecord OnCommandListBegin(CommandList commandList, GpuTiming_CommandListRecord previousRecord)
        {
            if (previousRecord != null) abandon(previousRecord);   // Begin() again without a submit

            var activeMode = mode;
            if (activeMode == GpuTiming_Mode.Off) return null;

            GpuTiming_CommandListRecord record;
            GpuTiming_SlotBlock block;
            lock (poolLock)
            {
                if (slotsPerCommandList == 0) configureTrackerPoolLocked();
                drainFinalizedLocked();

                if (freeBlocks.Count > 0) block = freeBlocks.Pop();
                else if (liveBlocks < blockCap)
                {
                    TimestampQueryPool trackerPool;
                    try { trackerPool = device.ResourceFactory.CreateGpuTimingBlockPool(slotsPerCommandList); }
                    catch (Exception e) { faultLocked("creating a tracker's query pool", e); droppedCommandLists++; return null; }
                    block = new GpuTiming_SlotBlock(this, trackerPool, poolGeneration);
                    liveBlocks++;
                }
                else { droppedCommandLists++; return null; }

                record = recordPool.Count > 0 ? recordPool.Pop() : new GpuTiming_CommandListRecord();
                record.Mode = activeMode;
                record.Block = block;
                record.MaxGroups = (slotsPerCommandList - 2) / 2;
            }

            try
            {
                // F6: this recording resets its own block (Resolved -> Fresh), in its own command buffer, before any render pass can be open.
                commandList.GpuTiming_ResetBlockSlots(block.Pool);

                // Slot layout within the block: [0]=cl start, [1]=cl end, then (start,end) per debug group in push order.
                if ((activeMode & GpuTiming_Mode.CommandLists) != 0)
                {
                    commandList.WriteTimestamp(block.Pool, 0);
                    record.CommandListTimed = true;
                }
                record.UsedSlots = 2;   // the pair is reserved even when only groups are timed, keeps the layout fixed
                return record;
            }
            catch (Exception e)
            {
                // The automatic layer only ever writes Fresh slots of a block it alone resets, so a throw here is a profiler bug or a dying device - never the client's.
                faultUnlocked("starting a recording", e);
                lock (poolLock) { block.Pool.DiscardRange(0, 2); returnBlockLocked(block); recordPool.Push(record.Clear()); }
                return null;
            }
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

            uint startSlot = 2 + 2 * (uint)record.Groups.Count;
            record.Groups.Add(new GpuTiming_DebugGroupEntry(name, record.OpenGroups.Count - countUntimed(record.OpenGroups), startSlot));
            record.OpenGroups.Push(record.Groups.Count - 1);
            record.UsedSlots = Math.Max(record.UsedSlots, 2 + 2 * (uint)record.Groups.Count);
            writeGuarded(commandList, record, startSlot, "timing a debug group start");
        }

        /// <summary><see cref="CommandList.PopDebugGroup"/> hook.</summary>
        internal void OnPopDebugGroup(CommandList commandList, GpuTiming_CommandListRecord record)
        {
            if ((record.Mode & GpuTiming_Mode.DebugGroups) == 0 || record.OpenGroups.Count == 0) return;

            int groupIndex = record.OpenGroups.Pop();
            if (groupIndex < 0) return;   // untimed (over the per-list cap)

            var entry = record.Groups[groupIndex];
            writeGuarded(commandList, record, entry.StartSlot + 1, "timing a debug group end");
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
                writeGuarded(commandList, record, entry.StartSlot + 1, "closing an unclosed debug group");
                record.Groups[groupIndex] = entry with { Closed = true, Name = entry.Name + " (unclosed)" };
            }

            if (record.CommandListTimed) writeGuarded(commandList, record, 1, "timing a command list end");
            record.Ended = true;
        }

        /// <summary>
        ///     Every timestamp the automatic layer writes goes through here: a throw (profiler bug, dying device) faults the layer and marks the recording
        ///     <see cref="GpuTiming_CommandListRecord.Faulted"/> so <see cref="DrainCompleted"/> discards it instead of resolving half-written slots. The
        ///     client's recording continues untimed.
        /// </summary>
        private void writeGuarded(CommandList commandList, GpuTiming_CommandListRecord record, uint slot, string during)
        {
            if (record.Faulted) return;
            try { commandList.WriteTimestamp(record.Block.Pool, slot); }
            catch (Exception e) { record.Faulted = true; faultUnlocked(during, e); }
        }

        /// <summary><see cref="GraphicsDevice.SubmitCommands(CommandList)"/> hook, after the backend submitted. Numbers the submit and queues the record for resolve.</summary>
        internal void OnCommandListSubmitted(CommandList commandList, GpuTiming_CommandListRecord record)
        {
            record.CommandListName = commandList.Name;
            record.CpuSubmitTimestamp = Stopwatch.GetTimestamp();
            lock (poolLock)
            {
                record.Submit = new GpuTiming_SubmitIndex(nextSubmitIndex++);
                pending.Enqueue(record);
            }
        }

        private void abandon(GpuTiming_CommandListRecord record)
        {
            abandonedCommandLists++;
            record.Abandoned = true;
            lock (poolLock) pending.Enqueue(record);   // its block is returned when it reaches the head of the queue (DiscardRange first)
        }

        /// <summary>Freezes the tracker geometry and cap from the public knobs; opens a new pool generation.</summary>
        private void configureTrackerPoolLocked()
        {
            slotsPerCommandList = 2 + 2 * Math.Max(0u, MaxDebugGroupsPerCommandList);
            blockCap = Math.Max(1, MaxTrackedCommandLists);
            poolGeneration++;
        }

        /// <summary>
        ///     Resolved / abandoned / finalized: back to the cache. Its slots are Resolved or Fresh; the next owner's Begin() resets them (F6). A tracker from an
        ///     older generation (created before <see cref="ResetTrackerPool"/>) was uncounted when that generation closed: it is disposed without touching
        ///     <see cref="liveBlocks"/>, so the count stays exact for the current generation.
        /// </summary>
        private void returnBlockLocked(GpuTiming_SlotBlock block)
        {
            if (block == null) return;
            if (block.Generation != poolGeneration || slotsPerCommandList == 0) { disposeBlockLocked(block); return; }
            if (liveBlocks > blockCap) { disposeBlockLocked(block); return; }   // cannot happen unless the cap was lowered under it; keep the invariant anyway
            freeBlocks.Push(block);
        }

        /// <summary>
        ///     Takes every tracker the finalizer thread reported. It was resurrected by the finalizer, so it is either RE-ARMED (<see cref="GC.ReRegisterForFinalize"/>,
        ///     so a future loss reports again) and re-pooled - the common case, no driver call - or disposed (older generation, or <paramref name="disposeAll"/>).
        ///     App thread, under <see cref="poolLock"/>.
        /// </summary>
        private void drainFinalizedLocked(bool disposeAll = false)
        {
            while (finalizedBlocks.TryDequeue(out GpuTiming_SlotBlock block))
            {
                finalizedBlocksTotal++;
                if (disposeAll || block.Generation != poolGeneration || slotsPerCommandList == 0)
                {
                    disposeBlockLocked(block);   // finalizer already ran; nothing to suppress
                    continue;
                }

                GC.ReRegisterForFinalize(block);
                freeBlocks.Push(block);
            }
        }

        /// <summary>Destroys the tracker's queries through the device's normal path (app thread, under <see cref="poolLock"/>). Only current-generation trackers are counted in <see cref="liveBlocks"/>.</summary>
        private void disposeBlockLocked(GpuTiming_SlotBlock block)
        {
            GC.SuppressFinalize(block);
            if (block.Generation == poolGeneration) liveBlocks--;
            try { block.Pool.Dispose(); }
            catch (Exception e) { faultLocked("disposing a tracker's query pool", e); }
        }

        /// <summary>
        ///     The profiler must never take the client down: ANY exception raised by the timing layer's own device calls is recorded here, timing is switched
        ///     <see cref="GpuTiming_Mode.Off"/>, and the call returns as if timing had been off. <see cref="Fault"/> carries the message; the app clears it by
        ///     calling <see cref="ResetTrackerPool"/> and setting <see cref="Mode"/> again.
        /// </summary>
        private void faultLocked(string during, Exception e)
        {
            mode = GpuTiming_Mode.Off;
            faults++;
            if (fault == null) fault = $"GpuTiming disabled itself while {during}: {e.GetType().Name}: {e.Message}";
        }

        private void faultUnlocked(string during, Exception e)
        {
            lock (poolLock) faultLocked(during, e);
        }

        private static void emitSpans(GpuTiming_CommandListRecord record, ReadOnlySpan<ulong> ticks, double ticksPerSecond,
            List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups)
        {
            // ticks[0] corresponds to record.ResolveFirstSlot (see DrainCompleted), not necessarily to slot 0.
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

    /// <summary>
    ///     Section 10 F2/F5: ONE recording's timestamp storage - a self-contained <see cref="TimestampQueryPool"/> of <c>2 + 2 x MaxDebugGroups</c> slots, not
    ///     registered for the device's implicit reset (its owner resets it). Reachable only through the <see cref="CommandList"/> that holds it (and the pending
    ///     queue while submitted) or the owner's free cache; when a command list is garbage-collected with a live recording, the finalizer reports the tracker to
    ///     its <see cref="GpuTiming"/> and does nothing else - never a driver call on the finalizer thread.
    /// </summary>
    internal sealed class GpuTiming_SlotBlock
    {
        public readonly TimestampQueryPool Pool;
        /// <summary>The <see cref="GpuTiming"/> pool generation this tracker was created in; a tracker returning after <see cref="GpuTiming.ResetTrackerPool"/> belongs to an older generation and is disposed, not re-pooled or counted.</summary>
        public readonly int Generation;
        private readonly GpuTiming owner;

        public GpuTiming_SlotBlock(GpuTiming owner, TimestampQueryPool pool, int generation)
        {
            this.owner = owner;
            Pool = pool;
            Generation = generation;
            pool.Name = "Veldrid.GpuTiming.Tracker";
        }

        ~GpuTiming_SlotBlock()
        {
            owner.ReportFinalized(this);
        }
    }

    /// <summary>One timed debug group inside a <see cref="GpuTiming_CommandListRecord"/>; <c>EndSlot = StartSlot + 1</c>. Slots are block-relative.</summary>
    internal readonly record struct GpuTiming_DebugGroupEntry(string Name, int Depth, uint StartSlot, bool Closed = false);

    /// <summary>Per-recording state of a traced <see cref="CommandList"/>; owned by the command list between Begin() and the submit that queues it. Holds the recording's <see cref="Block"/>.</summary>
    internal sealed class GpuTiming_CommandListRecord
    {
        public GpuTiming_Mode Mode;
        /// <summary>The recording's own slot storage (section 10). Slot indices in this record are relative to it (the command-list pair is slots 0 / 1).</summary>
        public GpuTiming_SlotBlock Block;
        public uint UsedSlots;          // 2 + 2 * timed groups; the prefix of the block that was actually written
        public uint MaxGroups;
        public bool CommandListTimed;
        public bool Ended;
        public bool Abandoned;
        /// <summary>A timestamp write threw after the recording started (see <see cref="GpuTiming.Fault"/>): the slots are in an unknown written state; drain discards rather than resolves.</summary>
        public bool Faulted;

        /// <summary>First slot that was actually written: the reserved command-list pair is skipped when only debug groups are timed.</summary>
        public uint ResolveFirstSlot => CommandListTimed ? 0u : 2u;

        /// <summary>Number of written slots from <see cref="ResolveFirstSlot"/>; 0 when nothing was timed in this recording.</summary>
        public uint WrittenSlotCount => UsedSlots - ResolveFirstSlot;
        public string CommandListName;
        public long CpuSubmitTimestamp;
        public GpuTiming_SubmitIndex Submit;
        public readonly List<GpuTiming_DebugGroupEntry> Groups = new List<GpuTiming_DebugGroupEntry>();
        public readonly Stack<int> OpenGroups = new Stack<int>();   // indices into Groups; -1 = pushed past the cap (untimed)

        public GpuTiming_CommandListRecord Clear()
        {
            Mode = GpuTiming_Mode.Off;
            Block = null;
            UsedSlots = MaxGroups = 0;
            CommandListTimed = Ended = Abandoned = Faulted = false;
            CommandListName = null;
            CpuSubmitTimestamp = 0;
            Submit = default;
            Groups.Clear();
            OpenGroups.Clear();
            return this;
        }
    }
}