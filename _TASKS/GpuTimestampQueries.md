# TASK: GPU timestamp queries + automatic GPU timing - the fork half of per-pass GPU timing

Filed Nov 9 2026 from AN_Monsters; revised Nov 10 2026 after design review (decisions in section 2). The kit half (the per-frame CPU/GPU pass table
built on this) is `AN_Monsters/_SPECS_RenderKit/95_GpuTiming.md`.

Status: **IMPLEMENTED Nov 10 2026** (commits `7812365c` primitive, `b2eb475f` Begin/End refactor, + automatic layer). `Veldrid.ContractTests`
127/127 green on D3D11 + Vulkan (34 timestamp tests: 20 explicit-pool, 14 automatic-layer). Remaining: publish to LocalNuGet, then the kit half.

Implementation notes that changed the spec text below are marked **(impl)**. Two findings worth reading first: the per-slot reset rule (5.3) and the
D3D11 multi-device query hazard (8, "Driver finding").

## 1. Why

Veldrid (and this fork) exposes NO GPU timing primitive: no timestamp query, no pipeline statistics, nothing. Every "where is the frame going?" question in the
engine is answered today by CPU stopwatches around `CommandList` recording and `SubmitCommands`, which only reflect GPU time when a submit or a `Map(READ)` blocks -
and then without saying WHICH pass. On Nov 9 that cost six 30-second bisection runs (toggling features through environment variables) to localise ONE compute
dispatch; with timestamps it is one line in a stats table. The engine renders through 5-8 passes per frame (GBuffer, shadow cascades, compute feedback, lighting,
compose, translucent) and every one of them needs its own GPU number.

End goal (user, Nov 10): when GPU profiling is enabled, **every submitted command list is traced automatically** - the app marks nothing - and the output is good
enough to draw an Nsight-style timeline (command-buffer lane + marker lane) and a per-pass table.

## 2. Decisions (Nov 10 review)

| # | Decision |
|---|---|
| A1 | `TimestampQueryPool` is modelled on `Fence`: `abstract class : IDeviceResource, IDisposable`, abstract `Name`, `IsDisposed`, `Dispose`. There is no `DeviceResource` base class in the fork. |
| A2 | Resolve returns a single enum `TimestampResolveResult { NotReady, Ready, Disjoint }` - never `true` with zeroed ticks. |
| A3 | No public `Reset`. Slot reset is fully implicit and behaves identically on D3D11 and Vulkan (see 5.3). |
| A4 | Re-writing a slot that is written-but-unresolved is LEGAL: the old result is abandoned and `TimestampQueryPool.AbandonedWriteCount` is incremented. No throw. Callers size rings deep (cheap). |
| A5 | Two layers: (1) the explicit pool primitive (kept as specced), (2) a device-owned **automatic timing** layer built on it that brackets every `CommandList` and every debug group with no app marking. |
| B1 | D3D11: one `TIMESTAMP_DISJOINT` bracket per command list recording (Begin()..End()). A shared pool records per slot WHICH disjoint window wrote it (`slotDisjointWindow[]`) so a pool may be shared across command lists. |
| B2 | A resolve range whose slots span disjoint windows with different frequencies, or any window flagged disjoint, returns `Disjoint`. |
| B3 | `GetData` runs on the immediate context under `immediateContextLock` (same lock as `SubmitCommandsCore`). |
| B6 | Vulkan ticks are masked to the graphics queue family's `timestampValidBits` inside the fork; callers get already-masked values. |
| B7 | `Features.TimestampQueries` on Vulkan = selected graphics queue family `timestampValidBits != 0` (NOT `timestampComputeAndGraphics`). |
| B8 | A resolve range with ANY slot unavailable returns `NotReady`. |
| C2 | Tests live in `src/Veldrid.ContractTests` (D3D11 + Vulkan via `ShapeTestHarness.CreateDevice`). Probe workload = N x 64 MB CopyBuffer, no shader (6.1; the compute-bundle idea was dropped). |
| C3 | The "not ready immediately after submit" test is restated: `NotReady` BEFORE submit; after submit only "returns promptly, never throws". |
| C4 | `AN.Veldrid.stableapi` snapshot is regenerated and committed with the API. |
| C5 | All four `new GraphicsDeviceFeatures(...)` call sites switch to NAMED arguments while adding `timestampQueries`. No behaviour change. |
| D1 | No pipeline-stage parameter. Semantics are end-of-pipe (`BOTTOM_OF_PIPE` on Vulkan; D3D11 timestamps are end-of-pipe by definition). |
| E1 | No binding changes: ppy.Vk 1.0.26 has every Vulkan entry point needed (`vkCreateQueryPool`, `vkDestroyQueryPool`, `vkCmdResetQueryPool`, `vkCmdWriteTimestamp`, `vkGetQueryPoolResults`, `timestampValidBits`, `timestampPeriod`). Host query reset (`vkResetQueryPool`) is absent and NOT needed because resets are recorded at `CommandList.Begin()`. Vortice.Direct3D11 2.4.2 has `ID3D11Query`, `QueryType.Timestamp/TimestampDisjoint`, `AsyncGetDataFlags.DoNotFlush`. |

## 3. Public API - layer 1: explicit pool primitive (`Veldrid`)

```csharp
/// Status of a timestamp resolve. Never blocks; NotReady means ask again next frame.
public enum TimestampResolveResult { NotReady = 0, Ready = 1, Disjoint = 2 }

/// A fixed-size set of GPU timestamp slots; the app writes slot indices into a command list and resolves them a few frames later.
public abstract class TimestampQueryPool : IDeviceResource, IDisposable
{
    public abstract uint Capacity { get; }
    /// Count of writes that overwrote a slot whose previous result was never resolved (ring too shallow). Diagnostic only.
    public abstract ulong AbandonedWriteCount { get; }
    public abstract string Name { get; set; }
    public abstract bool IsDisposed { get; }
    public abstract void Dispose();
}

public abstract partial class ResourceFactory
{
    public TimestampQueryPool CreateTimestampQueryPool(uint capacity);   // throws VeldridException if !Features.TimestampQueries
}

public abstract partial class CommandList
{
    /// Records a GPU timestamp into pool[index] at the point this command executes (end-of-pipe: everything recorded before has finished).
    /// No-op when !Features.TimestampQueries.
    public void WriteTimestamp(TimestampQueryPool pool, uint index);
}

public abstract partial class GraphicsDevice
{
    /// Non-blocking. Ready: every slot in [first, first + count) has been written by the GPU and `ticks` is filled (raw GPU ticks, already
    /// masked to the valid bit width); `ticksPerSecond` converts them. NotReady: ask again next frame. Disjoint (D3D11): the GPU clock changed
    /// during the window; discard the range. Never blocks, never throws for a timing reason.
    public TimestampResolveResult TryResolveTimestamps(TimestampQueryPool pool, uint first, uint count, Span<ulong> ticks, out double ticksPerSecond);
}

// GraphicsDeviceFeatures
public bool TimestampQueries { get; }
```

Contract notes:
- Ticks are **device-absolute** (same clock for every pool on the device): spans from different pools/frames can be laid on one timeline.
- A pool reused each frame must be ring-buffered by the CALLER (the automatic layer does this for its own pools). The API does not hide latency.
- `WriteTimestamp` with `index >= Capacity` throws `VeldridException` (`VALIDATE_USAGE`).

## 4. Public API - layer 2: automatic GPU timing (`Veldrid`)

Device-owned. Default OFF; when off there is exactly one bool check per `Begin/End/PushDebugGroup/PopDebugGroup/SubmitCommands`.

```csharp
[Flags]
public enum GpuTiming_Mode { Off = 0, CommandLists = 1, DebugGroups = 2 }

/// One completed command-list span (one per SubmitCommands while enabled).
public readonly record struct GpuTiming_CommandListSpan(
    GpuTiming_SubmitIndex Submit,          // monotonically increasing per device, order of SubmitCommands
    string CommandListName,                 // CommandList.Name at submit time
    ulong GpuStartTicks, ulong GpuEndTicks, // device-absolute, masked
    double TicksPerSecond,
    long CpuSubmitTimestamp);               // Stopwatch.GetTimestamp() taken inside SubmitCommands (CPU-side ordering / submit-wait correlation)

/// One completed debug-group span. Nested groups appear with increasing Depth; ParentSubmit links to the enclosing command list span.
public readonly record struct GpuTiming_DebugGroupSpan(
    GpuTiming_SubmitIndex ParentSubmit,
    string GroupName,
    int Depth,                              // 0 = outermost group in that command list
    ulong GpuStartTicks, ulong GpuEndTicks,
    double TicksPerSecond);

public readonly record struct GpuTiming_SubmitIndex(ulong Value);

public sealed class GpuTiming   // GraphicsDevice.GpuTiming
{
    public bool Supported { get; }                          // Features.TimestampQueries
    public GpuTiming_Mode Mode { get; set; }                 // runtime toggle; forced Off when !Supported; turning ON creates the ring (impl: must
                                                             // happen before the next Begin() so that Begin() records the ring's initial reset)
    public uint RingSlotPairs { get; set; } = 2048;          // (impl) ring capacity in (start,end) pairs; applied when the ring is (re)created
    public uint MaxDebugGroupsPerCommandList { get; set; } = 16;   // (impl) pairs reserved per recording for groups; further pushes are not timed
    public ulong DroppedCommandLists { get; }                // recordings traced WITHOUT timestamps because the ring was full (never blocks)
    public ulong DisjointCommandLists { get; }               // recordings discarded because D3D11 reported a clock change
    public ulong UntimedDebugGroups { get; }                 // (impl) pushes past MaxDebugGroupsPerCommandList (forwarded to the debugger, not timed)
    public ulong AbandonedCommandLists { get; }              // (impl) Begin()'d while on, never submitted; slots recycled unread
    /// Moves every span whose results are now available into the lists (appends; caller clears). Non-blocking: stops at the first pending
    /// recording, in submit order, whose values are not in yet. Called once per frame by the kit.
    public void DrainCompleted(List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups);
    /// (impl) Disposes the ring (Mode forced Off, pending spans dropped) so the next enable rebuilds it from RingSlotPairs / MaxDebugGroups...
    public void RecreateRing();
}
```

Semantics (as built):
- `Mode has CommandLists`: `CommandList.Begin()` (after the backend's `BeginCore`) writes the start timestamp; `End()` (before `EndCore`, so the buffer is
  still open) writes the end timestamp; `SubmitCommands` stamps `Stopwatch.GetTimestamp()`, assigns the `GpuTiming_SubmitIndex` and queues the record.
- `Mode has DebugGroups`: `PushDebugGroup(name)` writes a start timestamp (BEFORE the backend marker, so the span brackets exactly what the debugger
  shows) and pushes on the recording's stack; `PopDebugGroup()` writes the end timestamp (after the marker pop). Groups still open at `End()` are closed
  there and reported with the name suffixed `" (unclosed)"`. Pushes beyond `MaxDebugGroupsPerCommandList` (count per recording, nested or sequential)
  are forwarded to the debugger but not timed (`UntimedDebugGroups++`). Group writes are legal inside render passes because their slots are Fresh
  (reset at `Begin()`).
- **(impl) Slot allocation**: each traced recording reserves a contiguous block of `2 + 2 * MaxDebugGroupsPerCommandList` slots from ONE ring pool
  (`2 * RingSlotPairs` slots, `"Veldrid.GpuTiming.Ring"`) at `Begin()`. Layout `[clStart, clEnd, g0Start, g0End, ...]`. A block that does not fit before
  the end of the ring wraps to 0 and the wasted tail is charged to that record. If `used + block > capacity` the recording is NOT traced
  (`DroppedCommandLists++`). Defaults (2048 pairs, 16 groups) keep ~120 recordings in flight. Blocks are freed in submit (FIFO) order by `DrainCompleted`.
- **(impl) Resolve**: `DrainCompleted` resolves only the WRITTEN prefix of a block (`CommandLists` off => the first pair stays Fresh and is skipped),
  so a `DebugGroups`-only recording resolves. `Disjoint` => `DisjointCommandLists++`, spans dropped, block freed.
- **(impl) Abandon**: `Begin()` on a command list whose previous recording was never submitted marks that record abandoned; it is queued so its block
  is freed in ring order, and its written slots are `DiscardRange`d (-> Resolved without a read -> reset at the next Begin). `AbandonedCommandLists++`.
  A command list DISPOSED with a live record leaks its block for the life of the ring (no base-class Dispose hook); `RecreateRing()` clears it.
- Mode changes take effect at the next `Begin()`; a recording keeps the mode it started with.
- Nothing here requires `WaitForIdle`. Resolution happens only inside `DrainCompleted` (the "opportunistically in SwapBuffers" idea was dropped: one
  drain site keeps the lock discipline trivial).
- Threading: the ring allocator + pending queue are guarded by one lock (`Begin()`, `SubmitCommands`, `DrainCompleted`); per-recording state lives on
  the `CommandList` (`gpuTimingRecord`) and needs none; several command lists may record on different threads concurrently. The pools' own
  `StateLock` covers slot state (5.3 #4).
- Cost when `Off`: `Begin()` makes one call that returns on a volatile read; `End/Push/Pop/Submit` test a null field.
- **(impl) Plumbing**: `CommandList`'s internal ctor takes the device's `GpuTiming` (4 backend call sites); `GraphicsDevice` constructs it in its base
  ctor and disposes the ring in `Dispose()` before `PlatformDispose()`.

Not goals (separate future task `_TASKS/GpuPerformanceCounters.md`, not written): hardware counters (SM throughput, occupancy), per-draw attribution, stall
reasons, host/GPU clock calibration (`VK_EXT_calibrated_timestamps` - not in ppy.Vk; D3D11 has no equivalent). The `GpuTiming` surface must not grow into those.

## 5. Backend design

### 5.1 D3D11 (`src/Veldrid/D3D11`)

- `D3D11TimestampQueryPool`: `ID3D11Query[Capacity]` of `QueryType.Timestamp` created on `device`; `byte[] slotState`; `int[] slotDisjointWindow`;
  a small pool of `QueryType.TimestampDisjoint` queries (`DisjointWindow { ID3D11Query Query; bool Ended; }`) owned by the DEVICE (shared across pools).
- `D3D11CommandList.Begin()`: if any timestamp will be written (automatic mode on) or lazily at the first `WriteTimestamp`, acquire a disjoint window from the
  device pool and `DeviceContext.Begin(disjoint)` on the DEFERRED context. `End()` / `Reset()`: `DeviceContext.End(disjoint)`, mark window `Ended`.
- `WriteTimestamp(pool, i)`: `DeviceContext.End(pool.Queries[i])`; `pool.slotDisjointWindow[i] = currentWindow; slotState[i] = Written` (abandon rule A4).
- `TryResolveTimestamps`: under `immediateContextLock`: for each distinct window in the range `GetData(window, out QueryDataTimestampDisjoint, DoNotFlush)` ->
  `S_FALSE` => `NotReady`; `Disjoint` flag or frequencies differ across windows => `Disjoint`; then per slot `GetData(query, out ulong, DoNotFlush)` ->
  `S_FALSE` => `NotReady`. All ready => fill `ticks`, `ticksPerSecond = Frequency`, mark slots `Resolved`, return `Ready`.
- Disjoint windows are returned to the device pool when every slot that references them is resolved or abandoned (refcount per window).
- **(impl)** `TryResolveTimestamps` uses `GetData` flags `None`, not `DO_NOT_FLUSH`: this fork's D3D11 `WaitForIdleCore` is a no-op and
  `ExecuteCommandList` does not flush, so an unflushed immediate context would read `S_FALSE` forever. `GetData`'s implicit Flush is asynchronous.
- **(impl)** Unfetched query objects are never re-issued (renamed instead) - see section 8, "Driver finding".
- Hazards: queries created on the immediate device and `End`ed on a deferred context are supported since FL10 (`D3D11_FEATURE_THREADING` not required).
  `Features.TimestampQueries = true` on D3D11 (FL10+; the fork requires FL11 already).

### 5.2 Vulkan (`src/Veldrid/Vk`)

- `VkTimestampQueryPool`: `vkCreateQueryPool(VK_QUERY_TYPE_TIMESTAMP, Capacity)`; `byte[] slotState`; `ulong validBitsMask`.
- `WriteTimestamp(pool, i)`: `vkCmdWriteTimestamp(cb, BOTTOM_OF_PIPE, pool, i)`. Reset rule in 5.3.
- `TryResolveTimestamps`: `vkGetQueryPoolResults(first, count, stride 16, VK_QUERY_RESULT_64_BIT | WITH_AVAILABILITY_BIT)` WITHOUT `WAIT_BIT`;
  any availability == 0 => `NotReady`; else mask each value with `validBitsMask`, `ticksPerSecond = 1e9 / physicalDeviceProperties.limits.timestampPeriod`,
  return `Ready`. (`Disjoint` is never returned on Vulkan.)
- Device creation: `Features.TimestampQueries = qfp[graphicsQueueIndex].timestampValidBits != 0` (B7); store `timestampValidBits` for the mask.

### 5.3 Slot reset - identical behaviour on both backends (A3)

Per-slot state: `Fresh -> Written -> Resolved`. Vulkan requires a `vkCmdResetQueryPool` before a slot can be written again; D3D11 does not, but the rule is
enforced identically for parity. (Revised during implementation, Nov 10: the first draft said "reset the WHOLE pool at the first write of a command
list" - that would wipe pending results of other command lists sharing the pool, including the automatic layer's own ring. The rule is per-slot.)

1. **`Fresh` slot** (reset on the GPU and not written since): the write is legal anywhere, including inside a render pass. New pools start with every slot
   `Resolved` (Vulkan: undefined until first reset) so rule 2 performs the initial reset.
2. **`Resolved` slots are reset lazily at the next `CommandList.Begin()` on ANY command list** (`VkCommandList.recordPendingTimestampResets`): Begin() is
   always outside a render pass, and queue order guarantees the reset executes after the command list whose values were read. The device keeps a
   snapshot list of live pools (`VkGraphicsDevice.TimestampPoolsSnapshot`, lock-free poll, empty when the app never created a pool). Contiguous slots are
   coalesced into one `vkCmdResetQueryPool` each. D3D11: the state transition only (no GPU work needed).
3. **Rewriting a non-`Fresh` slot** (a `Written` value never resolved = abandoned, `AbandonedWriteCount++`; or `Resolved` but no Begin() happened since):
   outside a render pass -> `vkCmdResetQueryPool(slot, 1)` + write (D3D11: just write); inside a render pass -> cannot reset on Vulkan -> throw
   `VeldridException` under `VALIDATE_USAGE` on BOTH backends, skip the write (state restored) in non-validating builds. D3D11 mirrors Vulkan's lazy
   render pass with `D3D11CommandList.emulatedRenderPassActive` (set by draws/clears, cleared by SetFramebuffer/Dispatch/Copy*/Resolve/Begin/End).
4. Slot state is shared between the recording thread and the resolving thread; every transition takes the pool's `StateLock`, and the Vulkan resolve
   holds it across `vkGetQueryPoolResults` + the state change so a concurrent Begin() cannot reset a slot mid-read.

Why not host reset: `vkResetQueryPool` is not bound in ppy.Vk 1.0.26 and would need Vulkan 1.2 / `VK_EXT_host_query_reset`; the Begin()-reset path needs
neither and is the same cost. Revisit only if a use case needs reset without a command list.

### 5.4 Automatic layer internals (`src/Veldrid/GpuTiming.cs`, backend-agnostic)

As built (details in section 4 "Semantics (as built)"): one ring pool, per-recording reserved blocks, FIFO `pending` queue of
`GpuTiming_CommandListRecord` (pooled; fields `FirstSlot/ReservedSlots/WasteSlots/UsedSlots/MaxGroups`, `Groups` list of `GpuTiming_DebugGroupEntry
(Name, Depth, StartSlot, Closed)`, `OpenGroups` stack with `-1` = untimed), `DrainCompleted` pops from the head while resolve says `Ready`/`Disjoint`.
Hooks: `CommandList.Begin()/End()` became non-virtual and delegate to new `BeginCore()/EndCore()` abstracts (commit `b2eb475f`, the "preferred" option);
`PushDebugGroup/PopDebugGroup/SubmitCommands` were already non-virtual. Why reserve a whole block at `Begin()` instead of allocating per push: all of a
recording's slots are then guaranteed Fresh (resolved + reset before the block was handed out) even for pushes that happen inside a render pass, where a
non-Fresh slot could not be reset.

### 5.5 Metal / OpenGL

Out of scope for the first cut: `Features.TimestampQueries = false`, `WriteTimestamp` no-op, `TryResolveTimestamps` returns `NotReady`,
`CreateTimestampQueryPool` throws, `GpuTiming.Supported = false` and `DrainCompleted` returns nothing. (Metal: `MTLCounterSampleBuffer`; OpenGL:
`GL_TIMESTAMP` queries - later.)

## 6. Tests (`src/Veldrid.ContractTests`)

### 6.1 Probe shader

### 6.1 Probe workload (no shader)

`TimestampProbe_Workload.Record(CommandList cl, int repeats)`: `repeats` x `CopyBuffer` of a 64 MB `BufferUsage.Staging`-free device buffer pair
(`src -> dst`, full range). Pure GPU work, linear in `repeats`, identical API on both backends, nothing to compile. The compute-shader variant
(`timestamp_probe.comp.glsl`, 4096 invocations x `Iterations` dependent FMAs) was considered and dropped for the first cut: `.vdshader` bundles are built by the
standalone `src/Veldrid.ImGui/Scripts/CompileShaders.csproj` against the PUBLISHED `ArtificialNecessity.Veldrid.SPIRV` package and committed by hand - a
dependency tax the probe does not need. Add it later only if copy bandwidth proves too noisy on some adapter. (The existing `Fill` bundle in
`ComputeBundleAndVertexInfoTests` is serialization-only and has no tunable cost.)

### 6.2 Cases (each `[Theory]` over D3D11 + Vulkan; skip when `!Features.TimestampQueries`)

All implemented Nov 10 (`TimestampQueryTests` 10 x 2, `GpuTimingAutoTests` 7 x 2); the whole assembly runs serially (`TestParallelism.cs`; see 8).
Tests that need "inside a render pass" use `TimestampProbe_DrawScene`: a real triangle through the committed ImGui bundle, because Vulkan opens its
pass at the first DRAW (clears are deferred into the load-op). D3D11's `WaitForIdle()` is a no-op in this fork, so resolves poll with a 2-3 s deadline.

- [x] `ExplicitPool_TwoTimestampsAroundWorkload_T1GreaterThanT0` - pool(2); `WriteTimestamp(0)`, probe(N), `WriteTimestamp(1)`; `WaitForIdle`;
      `TryResolveTimestamps` == `Ready`, `t1 > t0`, `ticksPerSecond > 0`.
- [x] `ExplicitPool_DoublingRepeatsRoughlyDoublesDelta` - warm-up, then best-of-3 at N=8 and 2N=16 copies, delta ratio in [1.5, 3.0].
- [x] `ExplicitPool_NotReadyBeforeSubmit_ReadyAfterWait` - record both timestamps, `End()`, do NOT submit -> `NotReady`; then submit -> `Ready`.
- [x] `ExplicitPool_ResolveRightAfterSubmit_ReturnsPromptlyNeverThrows` - call immediately after submit; elapsed < 50 ms and result is a defined
      enum value. (C3 restatement.)
- [x] `ExplicitPool_ReuseInsideRenderPass_Throws` - write slot 0 in CL #1, submit, wait; CL #2: `BeginPassAndDraw` (a DRAW, not a clear) then
      `WriteTimestamp(0)` -> `VeldridException` on BOTH backends; the refused write left the first value resolvable.
- [x] `ExplicitPool_FreshSlotInsideRenderPass_IsLegal` (impl) - 3 frames of writes between draws; resolved slots are Fresh again at the next Begin; 0 abandons.
- [x] `ExplicitPool_ResolvedSlot_IsResetAtNextBegin_AndReusable` (impl) - 5 frames reusing a 2-slot pool; 0 abandons.
- [x] `ExplicitPool_OverwriteUnresolved_IncrementsAbandonedWriteCount` - write slot 0 twice across two command lists without resolving -> count == 1.
- [x] `ExplicitPool_Validation_RejectsBadArguments` (impl) - capacity 0, index >= Capacity, range > Capacity, ticks span too short.
- [x] `Auto_CommandListMode_EverySubmitProducesOneSpan` - enable `CommandLists`; submit 3 named command lists; `DrainCompleted` -> 3 spans,
      names match, `Submit` indices increasing, `GpuEndTicks > GpuStartTicks`, spans non-overlapping and in submit order.
- [x] `Auto_DebugGroupMode_NestedGroupsReportDepthAndContainment` - Outer { Inner } Sibling; Inner.Depth == 1, Inner inside Outer inside the CL span on
      the device timeline, Sibling after Outer.
- [x] `Auto_DebugGroupMode_InsideRenderPass_IsLegal` - 4 frames, two groups pushed/popped around draws inside the pass; 8 group spans, 0 drops.
- [x] `Auto_UnclosedGroup_IsClosedAtEndAndFlagged` (impl) - `DebugGroups`-only mode, push without pop -> one span named `"Forgotten (unclosed)"`, no CL span.
- [x] `Auto_ModeOff_ProducesNothing` - `Mode = Off`; submit with push/pop; drain -> empty; `DroppedCommandLists == 0`.
- [x] `Auto_RingFull_DropsNeverBlocks` - `RingSlotPairs = 2`, `MaxDebugGroupsPerCommandList = 0`; 10 submits without draining -> `DroppedCommandLists == 8`,
      prompt; after a drain the ring traces again.
- [x] `Auto_BeginWithoutSubmit_IsAbandonedAndRingRecovers` (impl) - `AbandonedCommandLists == 1`; then a 4-pair ring with periodic abandons keeps
      tracing in-pass groups across 6 laps (abandoned slots reset at the next Begin).
- [x] `Features_TimestampQueries_ReportedTrue` - D3D11 and Vulkan (Metal/OpenGL `false` is asserted by construction in 5.5; no CI device for them).

## 7. Implementation plan (incremental, each step builds + tests green)

- [x] 7.1 `GraphicsDeviceFeatures.TimestampQueries` + NAMED arguments at all four construction sites (C5). Metal/OpenGL pass `false`.
- [x] 7.2 `TimestampResolveResult`, `TimestampQueryPool`, `ResourceFactory.CreateTimestampQueryPool` (+ abstract `CreateTimestampQueryPoolCore`),
      `CommandList.WriteTimestamp` (+ `WriteTimestampCore`), `GraphicsDevice.TryResolveTimestamps` (+ core). Metal/OpenGL stubs (5.5).
- [x] 7.3 Vulkan implementation (5.2 + 5.3). Feature bit from queue family.
- [x] 7.4 D3D11 implementation (5.1 + 5.3) including disjoint window pool.
- [x] 7.5 Probe workload + explicit-pool tests (6.1; `TimestampQueryTests`, 10 cases x 2 backends).
- [x] 7.6 `CommandList.Begin/End` -> `BeginCore/EndCore` refactor (5.4) - pure refactor, no behaviour change, tests still green.
- [x] 7.7 `GpuTiming` automatic layer (section 4, 5.4; `src/Veldrid/GpuTiming.cs`) + `GpuTimingAutoTests` (7 cases x 2 backends).
- [x] 7.8 Regenerate `AN.Veldrid.stableapi` (C4); this doc's status. Hand off to `95_GpuTiming.md` (next: publish to LocalNuGet).
- [ ] 7.9 Publish `ArtificialNecessity.Veldrid` to LocalNuGet (`cmd/publish-local`), bump AN_Monsters / AN_FluidUI.

## 7b. Client guidance - what consumers must do to get per-pass rows

The `CommandLists` lane is fully automatic. The per-pass `DebugGroups` lane is only as good as the `PushDebugGroup`/`PopDebugGroup` brackets the client
records - and as of Nov 10 2026 **no client calls `PushDebugGroup` at all** (`AN_Monsters/src`: 0 call sites; `AN_FluidUI/src`: 0 call sites). Each client
therefore has a small spec-side obligation, tracked in its own repo:

| Client | Where passes are recorded today | Obligation | Tracking spec |
|---|---|---|---|
| AN_Monsters (RenderKit + all render modes) | `GameRenderMode*.Render` records 4-6 `CommandList`s per frame (`// ---- Pass N ----` comments), e.g. `Experiment_VoxelColumnTerrain/Rendering/GameRenderModeVoxelColumnTerrain.cs`, `SpaceGame/Rendering/GameRenderModeSpaceScene.cs`, `Experiment_SilPOM/Rendering/GameRenderModeSilPOM.cs` | Wrap each pass (and sub-passes inside `DeferredLightingPass`: GTAO / sun / points / compose) in `PushDebugGroup(name)`/`PopDebugGroup()` via the kit's `GpuFrameProfiler.Scope(cl, name)`; set `CommandList.Name` per pass so the automatic lane is readable too | `AN_Monsters/_SPECS_RenderKit/95_GpuTiming.md` |
| AN_FluidUI GameCompositor (also hosts ArcaneSiege) | `FluidUI.GameCompositor/CompositorThread.cs` records one composite `CommandList` per present (~L585) plus a copy list for readback (~L694); panels render on their own `FEmbeddedSurface` lists | Name the composite / panel / game command lists; push a debug group per blit stage (game blit, each panel blit, motion-vector pass in the test app) | `AN_FluidUI/_TASKS/GpuTiming_GameCompositor.md` (filed Nov 10) |
| SilkyNvg inside FluidUI | SilkyNvg frame flush; FluidUI runs it either on its own multi-OS-window swapchain pump, embedded inside GameCompositor, or headless | DEFERRED (user, Nov 10) until the GameCompositor and AN_Monsters targets are done - the three hosting modes need their own look. First cut will be: name the NanoVG command list, one debug group per `Flush` | not yet filed |

Rules for clients (so the fork's output stays well-formed):
- Push/pop MUST balance inside a single `CommandList` recording; a group left open is closed at `End()` and reported as `"<name> (unclosed)"`.
- Nesting depth is capped at 16; deeper pushes are still forwarded to the debugger but not timed.
- Names should be stable string literals or interned `GpuPass_Name` values, not per-frame `$"..."` concatenations (they become dictionary keys in the stats table).
- Debug groups cost one timestamp write each (~tens of ns of GPU, one query slot). Dozens per frame are fine; per-draw groups are NOT the intended use.
- `PushDebugGroup` is also what RenderDoc / Nsight / PIX display, so this obligation pays twice.

## 8. Open items / references

### Driver finding (Nov 10 2026) - D3D11 multi-device query hazard

While landing the tests, `Veldrid.ContractTests` crashed intermittently with `AccessViolationException` inside
`ID3D11DeviceContext::FinishCommandList` (NVIDIA GeForce RTX 4080, driver 32.0.16.2101). Bisected with a two-thread probe (each thread its own
`GraphicsDevice`, 200 iterations of Begin / timestamps / 8 x 64 MB CopyBuffer / End / Submit), then with targeted follow-ups. **Every row below was
run; "pass" = 0 crashes in N.** The AV always surfaces in `FinishCommandList` (Veldrid `CommandList.End()`, CPU-side bake, before any submit).

| # | devices (threads) | what is concurrent | result |
|---|---|---|---|
| 1 | 1 | heavy copies + timestamps, never resolved | pass 3/3 |
| 2 | 1 | same + **300 forced compacting Gen2 collections** from a second thread (filler allocated before/around our objects, freed and `GC.Collect(compacting)` in a loop) | pass 5/5 |
| 3 | 2 | heavy copies, **no queries** | pass 3/3 (+4/4 later) |
| 4 | 2 | heavy copies + timestamps, resolved each iteration | **AV 2/3** |
| 5 | 2 | heavy copies + timestamps, never resolved (query objects renamed on every rewrite) | AV 3/3 before the renaming rule; pass 4/4 after it |
| 6 | 2 | **RAW Vortice** `Begin(disjoint)/End(ts)/End(ts)/End(disjoint)` + `GetData` on the same deferred/immediate contexts, **none of this fork's query code** | **AV 2/4** |
| 7 | 2 | row 4 + process-wide lock around `FinishCommandList` | **AV 4/4** |
| 8 | 2 | row 7 + `GetData` under the same lock | AV 3/4 |
| 9 | 2 | row 7 + a query object is NEVER re-issued (fresh `ID3D11Query` on every write) | AV 3/5 |
| 10 | 2 | full ContractTests with the WHOLE `CommandList.End()` under one process-wide lock | AV 2/8 |
| 11 | 2 | row 4 with the **GC suppressed** (`GC.TryStartNoGCRegion` held for the whole run, never exited early) | **AV 4/5** |
| 12 | 2 | rows 4/5/3 on **Vulkan** | pass 4/4, 4/4, 4/4 |
| 13 | 2 | full ContractTests, the two timestamp classes serialised against each other but parallel with the other 93 D3D11 tests (non-query work) | AV 1/4 |
| 14 | 2 | full ContractTests, assembly-level `DisableTestParallelization` | pass 127/127 x 9 (0 aborts) |

**What the table eliminates, each by direct experiment:** Veldrid shared state (there is none across devices - the only statics in the D3D11
backend are a feature flag and a debug-name counter; command lists are created per `End()` and disposed after `ExecuteCommandList`, never pooled),
the bake as the unsafe region (row 7), the readback (row 8), query-object re-issue (row 9), the whole end-of-recording sequence (row 10), and the
garbage collector moving memory a native call thought was pinned (row 2 cannot provoke it on one device; row 11 still crashes with no GC at all).
Row 6 shows it with canonical raw API calls and none of this fork's query code; row 12 shows Vulkan immune.

**Conclusion:** the invariant across every crashing row is *two D3D11 devices concurrently active in one process, at least one issuing queries*.
The corruption happens inside the NVIDIA user-mode driver (`nvwgf2umx.dll`, the one component shared by two devices) and `FinishCommandList` is
merely where it is detected. It is not fixable from this layer; it can only be avoided. Row 13 shows the second device need not use queries at
all - ordinary work on another device is enough - so a per-class test collection was insufficient.

**Mitigations in place:**
- `Veldrid.ContractTests`: `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (`TestParallelism.cs`, rationale inline). The suite
  takes ~8 s serial. Row 14.
- Process-wide unconditional locks around the bake on BOTH backends (`D3D11CommandList.EndCore` / `VkCommandList.EndCore`; approved by the user
  Nov 10, rationale in the code comments including the explicit rejection of a "skip when one device" shortcut). **They do not eliminate this
  crash (rows 7-10)** and are kept as an essentially free conservative measure - a serialised bake is one fewer variable - not as a fix.
- D3D11 pool: a query object whose result was never fetched is not re-issued (`D3D11TimestampQueryPool.MarkWritten`,
  `D3D11GraphicsDevice.ReturnTimestampDisjointWindow`). Fixed row 5; kept because it is the canonical issue -> GetData -> issue cycle.
- Production guidance: one `GraphicsDevice` per process (AN_Monsters, FluidUI GameCompositor) is unaffected; the GameCompositor's multi-threaded
  record/End/Submit on ONE device is the single-device case. A process that creates several D3D11 devices and keeps more than one active
  concurrently is at risk whether or not it times; not enforced in code (no safe cross-device coordination point exists in the fork).
- Not done, available on request (~1 h): a pure-Vortice console repro with no Veldrid at all, for an NVIDIA bug report.

### Probe workload finding (Nov 10 2026) - CopyBuffer is not a graphics-timeline clock on D3D11/NVIDIA

The explicit-pool ratio test first used N x 64 MB `CopyBuffer` as its workload. Measured deltas were super-linear: 2x copies into one destination
-> ratio 4-6; 4x copies over four rotating destinations -> ratio 13-15. Interpretation: on this driver buffer copies run on a copy/DMA engine that
timestamps written on the graphics pipeline do not bracket proportionally (Vulkan, single queue in Veldrid, was linear). The ratio test now uses
fill-rate-bound draws (8 vs 32 full-target triangles at 2048^2 through the ImGui bundle, median of 5, window [2, 8]) - the work timestamps are
designed to measure. `TimestampProbe_Workload` (copies) is still used where only "some GPU time passes" is needed. Guidance for the kit: GPU
spans around pure copies on D3D11 may under-report; bracket passes that contain draws/dispatches.

- `_BUGFIX/BUGFIX_Vulkan_CopyBuffer_NoPreCopyBarrier.md` is referenced here (and from `BUGFIX_D3D11_ComputeUavBindingList_Grows_PerFrame.md`) but was never
  filed anywhere under `C:\PROJECTS` (checked Nov 10). The only artefact is `_BUGFIX/BufferPolicies_NativeDocs/Vulkan_CopyBuffer.md`. Timestamps with
  BOTTOM_OF_PIPE are unaffected by that barrier gap either way (they measure completion, not visibility).
- Host/GPU clock calibration for a combined CPU+GPU timeline: not possible on D3D11; Vulkan needs `VK_EXT_calibrated_timestamps` (not bound). The
  `CpuSubmitTimestamp` in `GpuTiming_CommandListSpan` is the pragmatic anchor (GPU start >= CPU submit).
- If a future need arises for host query reset, fork ppy.Vk into `AN_VulkanBindings` (user approved in principle, deferred until necessary).

## 9. Hand-off

When this lands, `AN_Monsters/_SPECS_RenderKit/95_GpuTiming.md` describes the kit's `GpuFrameProfiler` as a drain-and-tabulate consumer of
`GraphicsDevice.GpuTiming` (its `Scope()` becomes `PushDebugGroup`/`PopDebugGroup` + CPU stopwatch, which also gives RenderDoc grouping for free), a
per-command-list lane, and the first consumer (the VoxelColumnTerrain bench's `[FrameStats]` line).