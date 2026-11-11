# BUGFIX: `GpuTiming` ring hands out a slot that is still `Written` -> `WriteTimestamp` throws one ring-wrap after a GPU idle

**Found:** 2026-11-10, AN_Monsters `Experiment_VoxelColumnTerrain` (`cmd\terrain-eroded.cmd --far-field split 80 --lod-levels 5`; also 17:56 and 18:24 runs). Three crashes, same shape:
`Veldrid.VeldridException: WriteTimestamp: slot N of TimestampQueryPool 'Veldrid.GpuTiming.Ring' is being reused inside a render pass (previous state Written)` thrown from
`GpuTiming.OnCommandListEnd` -> `CommandList.End()` on the game thread, each time ~40 frames (one ring wrap: 4096 slots / 34 per list / ~3 lists per frame) after a period in which
the GPU was idle (a 63 s / 110 s activation bake with timing enabled; a 300 ms render stall). The 16-minute steady runs never throw.
**Status:** FIXED IN FORK (Nov 10 19:30) - `_TASKS/GpuTimestampQueries.md` section 10 / 10.2b is the record of what landed. The pooled allocator of per-recording trackers
is in `src/Veldrid/GpuTiming.cs`; `Veldrid.ContractTests` 147/147 green D3D11 + Vulkan including the five section-10 tests (out-of-order submit, held list, lost list +
forced full GC, idle-then-burst, pending-timeout reclaim). Beyond the design below, the user added two requirements at review (Nov 10 19:00): **no "ring" vocabulary**
anywhere (`RingSlotPairs` -> `MaxTrackedCommandLists`, `RecreateRing` -> `ResetTrackerPool`), and **fail gracefully** - the layer is a performance debugger and must never
crash the client: every one of its own device calls is guarded, the first throw sets `GpuTiming.Fault` and forces `Mode = Off`; a dead resolve head is written off after
`PendingResolveTimeout` (`TimedOutCommandLists`). NOT yet done: publish + `AN_Monsters` bump + the consumer run below. Consumer-side mitigation in Monsters (enable timing
on the first rendered frame, not in activation) had moved the throw, not removed it.

## Where

`src/Veldrid/GpuTiming.cs`:
- `OnCommandListBegin` (L218-258): reserves a CONTIGUOUS block of `slotsPerCommandList` (= 2 + 2 x MaxDebugGroupsPerCommandList = 34) at `ringAllocHead`, in RING ORDER, at
  `Begin()` time; guards only with the COUNT `usedRingSlots + slotsPerCommandList > ringCapacitySlots` (-> `droppedCommandLists++`, return null).
- `DrainCompleted` (L118-161): frees records in `pending` order = SUBMIT order (or abandon time), `usedRingSlots -= ReservedSlots + WasteSlots`.
- The pool's per-slot state machine (`D3D11TimestampQueryPool.MarkWritten` / `VkTimestampQueryPool.MarkWritten`): a write to a `Written` slot inside a render pass throws
  under VALIDATE_USAGE (`_TASKS/GpuTimestampQueries.md` 5.3) - it is the pool refusing to corrupt a pending value. The throw is the symptom, not the bug.

## The bug: allocation order != release order, and a count cannot tell which slots are free

Veldrid places NO ordering constraint on command lists: a list may be `Begin()`'d now and submitted next Tuesday; several threads record concurrently (the Monsters bench has
three: game, compositor, FluidUI). The ring allocates by POSITION at `Begin()` and releases by COUNT in `Submit()` order. Those agree only when Begin order == Submit order.
When they differ, a ring-earlier block can belong to a LATER pending record; the count guard stays satisfied while `ringAllocHead` walks onto that block -> `Written` slot ->
throw. A block stays unresolved for a whole wrap whenever the drain stalls on a `NotReady` head (the first lists after a GPU idle report late), which is why every crash
followed an idle.

Two further consequences of the same design, independent of the crash:
1. A list that is `Begin()`'d and never submitted and never re-`Begin()`'d is never abandoned: its block is never released, `usedRingSlots` never comes back down, and the timer
   eventually drops EVERYTHING forever with no error (a leak the design cannot see).
2. The tail-waste accounting (`WasteSlots`) exists only because blocks must be contiguous in one ring.

### Why a guard is not the fix (considered, rejected)

Checking the block's slot states at `Begin()` and dropping when any is `Written` makes the throw impossible (the documented "recorded without timestamps" behaviour instead).
But with contiguous allocation the head CANNOT advance past a live block - it only advances by allocating - so one held command list sitting under the head turns into a
total stats blackout for every thread for as long as it is held, with 119 other blocks free. The guard converts a crash into a silent blackout under exactly the orderings
that cause the crash. Keep the per-slot check as the corruption fail-safe; do not call it the fix.

## Fix: a POOLED ALLOCATOR of self-contained per-list trackers, not a ring (user, Nov 10)

Every record already reserves the same fixed size. Replace the one shared `TimestampQueryPool` ring with a recycling cache of **trackers**, each a self-contained device object:

```
GpuTiming_CommandListRecord   (exists; owned by the CommandList between Begin() and the submit that queues it)
  + GpuTiming_SlotBlock Block  (NEW) - the tracker: ITS OWN query storage for one recording
      D3D11: ID3D11Query[34] + the rented disjoint window      (a regrouping of what the pool already does per slot)
      Vk:    its own VkQueryPool(34)                            (a slot of one big pool cannot be freed individually; a small pool per tracker can)
      + the per-slot state machine for its 34 slots (Fresh / Written / Resolved - the existing rule, now per block)
GpuTiming.freeBlocks : Stack<GpuTiming_SlotBlock>   (the pool = a cache; capacity = RingSlotPairs x 2 / 34 ~ 120 blocks, creates on demand up to a cap)
```

Ownership over a tracker's life - the ONLY references that ever exist:
```
freeBlocks  --Begin()-->  CommandList.record.Block  --Submit()-->  (+ pending until resolved)  --resolve / abandon-->  freeBlocks
```
- `Begin()`: pop ANY free block (create one if none and under the cap; else `droppedCommandLists++`, record without timestamps - unchanged contract). Order-agnostic by construction.
- Resolve (`DrainCompleted`) / abandon (`Begin()` again without a submit): return THAT block. No count, no head, no waste.
- **GC is the release for a lost list** (user's point): a tracker is reachable only through its `CommandList` (and `pending` while submitted). A list dropped on the floor -
  never submitted, never re-`Begin()`'d - takes its tracker with it. **The finalizer talks to NOBODY but the profiler** (user, Nov 10 18:48: "there is no reason for a random
  thread GC to be talking to the GPU driver"): the tracker keeps a reference to its `GpuTiming` owner and its finalizer does one thing - `owner.ReportFinalized(this)`, a push
  onto a `ConcurrentQueue<GpuTiming_SlotBlock>` (object resurrection; legal, the tracker is reachable again). `GpuTiming` drains that queue under `ringLock` on the next
  `Begin()` / `DrainCompleted()` - an app thread, inside the lock that already governs every other block transition - and decides:
    - default: **re-pool it**. Its queries are intact device objects, nothing GPU-side can reference a tracker that was collectable, so it goes straight back on `freeBlocks`
      (`GC.ReRegisterForFinalize` when it is handed out again). ZERO driver calls for the common case.
    - over the cap (the cache is already full): destroy its queries through the device's normal disposal path, on this thread.
  No lost-list bookkeeping, no finalizer-thread device access, no "device still alive" guard in the finalizer - that question moves to the drain, where the profiler already
  knows the answer (it IS the device's object).
- `pending` holding the reference while submitted is correct (results may still arrive). Pathological "submitted, never resolves" (device lost): cap `pending` and drop
  records older than N seconds -> releases the reference -> GC. Visible as `DroppedCommandLists`.
- `DrainCompleted`'s "stop at the first NotReady in submit order" stays - it is about EMISSION order (a later submit cannot have results before an earlier one), no longer about memory.
- Vk `Begin()` reset sweep: today `RecordPendingResets` runs over every registered pool; with ~120 small pools that is ~120 volatile reads of `HasSlotsAwaitingReset` per
  `Begin()` (cheap). Correct narrowing: a list resets ONLY its own tracker's block - the only block it will write. Do that in the same change.

### Files

- `src/Veldrid/GpuTiming.cs`: `ring*` fields, `createRingLocked`, `OnCommandListBegin` allocation, `DrainCompleted` release -> `freeBlocks` + `GpuTiming_SlotBlock`. `RingSlotPairs`
  becomes the block CAP (same public knob, documented as such). Hooks (`OnPushDebugGroup`, `OnPopDebugGroup`, `OnCommandListEnd`, `OnCommandListSubmitted`) and `emitSpans` unchanged
  except that slot indices are block-relative.
- `src/Veldrid/TimestampQueryPool.cs` + `D3D11/D3D11TimestampQueryPool.cs` + `Vk/VkTimestampQueryPool.cs`: a 34-slot pool IS the tracker's storage - no new pool type; the
  tracker owns one small pool. No finalizer on the pools themselves: the TRACKER has the finalizer, and it only reports to `GpuTiming`.
- `src/Veldrid/Vk/VkCommandList.cs` `Begin()`: reset the recording's own block instead of sweeping the registry.
- `src/Veldrid/CommandList.cs`: the record (and so the block) is already a field of the list - no change to ownership.
- `GpuTiming`: `ConcurrentQueue<GpuTiming_SlotBlock> finalizedBlocks` + `drainFinalizedLocked()` called at the top of `OnCommandListBegin` and `DrainCompleted`; `FinalizedBlocks` counter.

## Verification (to do)

`src/Veldrid.LifetimeTests/Program.cs` (the existing in-tree lifetime harness), both backends:
- `Test_GpuTiming_OutOfOrderSubmit_NoThrow`: three threads each `Begin / PushDebugGroup x4 / Pop / End` their own list; submits in REVERSE Begin order; 500 iterations; drain
  every iteration. Pre-fix: throws within a wrap. Post-fix: no exception, `DroppedCommandLists == 0`, every submit yields one command-list span.
- `Test_GpuTiming_HeldList_DoesNotBlackOut`: one list `Begin()`'d and never submitted for the whole test while another thread records 1000 timed lists. Pre-fix: stats stop
  once the head reaches the held block (or, with the rejected guard, immediately and forever). Post-fix: 1000 spans, `DroppedCommandLists == 0`.
- `Test_GpuTiming_LostList_IsCollected`: `Begin()` a list, drop every reference, `GC.Collect(); GC.WaitForPendingFinalizers();` -> the tracker's finalizer ran (test seam counter),
  no device call happened on the finalizer thread (assert via a thread-id seam), and the NEXT `Begin()` re-pooled the very same tracker (reference-equal) with no new query creation.
- `Test_GpuTiming_IdleThenBurst`: time 10 lists, `Thread.Sleep(2 s)` with no submits, then 200 lists - the crash's shape - no throw.
- Consumer: `cmd\terrain-eroded.cmd --far-field split 80 --lod-levels 5` in AN_Monsters survives the 110 s activation and runs > 5 minutes; `GpuTimingPanel` rows populated.

## Open questions

- Block cap when `freeBlocks` is empty: create up to `RingSlotPairs x 2 / 34` (~120) blocks total, then drop? Or grow unbounded (each block is 34 queries - cheap) and rely on
  `pending` cap + GC? Lean: cap at 2 x the old ring equivalent (~240) so a runaway recorder cannot allocate GPU queries without bound; drops are visible either way.
- Device teardown: `GpuTiming.DisposeRing()` (called from `GraphicsDevice.Dispose`) must drain `finalizedBlocks` and destroy every pooled tracker BEFORE the backend tears down; a
  tracker finalized AFTER that pushes onto a queue nobody drains - harmless (no device access in the finalizer by construction).