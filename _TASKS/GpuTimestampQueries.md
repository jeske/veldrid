# TASK: GPU timestamp queries + automatic GPU timing - the fork half of per-pass GPU timing

Filed Nov 9 2026 from AN_Monsters; revised Nov 10 2026 after design review (decisions in section 2). The kit half (the per-frame CPU/GPU pass table
built on this) is `AN_Monsters/_SPECS_RenderKit/95_GpuTiming.md`.

Status: SPEC APPROVED, implementation not started.

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
| C2 | Tests live in `src/Veldrid.ContractTests` (D3D11 + Vulkan via `ShapeTestHarness.CreateDevice`). A new probe compute bundle is added (6.1). |
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
    public bool Supported { get; }                       // Features.TimestampQueries
    public GpuTiming_Mode Mode { get; set; }              // runtime toggle; setting Off releases nothing, pools stay allocated once created
    public uint RingSlotPairs { get; set; } = 1024;       // capacity of the internal pool ring, in (start,end) pairs; applied on next enable
    public ulong DroppedCommandLists { get; }             // spans skipped because the ring was full (never blocks)
    public ulong DisjointCommandLists { get; }            // spans discarded because D3D11 reported a clock change
    /// Moves every span whose results are now available into the lists (appends; caller clears). Non-blocking. Called once per frame by the kit.
    public void DrainCompleted(List<GpuTiming_CommandListSpan> commandLists, List<GpuTiming_DebugGroupSpan> debugGroups);
}
```

Semantics:
- `Mode has CommandLists`: at `CommandList.Begin()` the fork writes a start timestamp, at `End()` an end timestamp; `SubmitCommands` records the pending span.
  A command list that is `Begin`/`End`ed but never submitted (or `Reset`) has its slots abandoned (A4) - no leak, no block.
- `Mode has DebugGroups`: `PushDebugGroup(name)` writes a start timestamp and pushes (name, slot) on a per-command-list stack; `PopDebugGroup()` writes the
  end timestamp. Groups still open at `End()` are closed at `End()` (and reported with the name suffixed `" (unclosed)"`). Depth > 15 is clamped/ignored
  (ring protection). Debug group timestamps are legal inside render passes on every backend (write only; the reset already happened at `Begin()`).
- Mode changes take effect at the next `Begin()`; a command list already recording keeps the mode it started with.
- Nothing here requires `WaitForIdle`. Resolution happens inside `DrainCompleted` (and opportunistically in `SwapBuffers`) through `TryResolveTimestamps`
  on the internal pools.
- Threading: `DrainCompleted` and `SubmitCommands` may be called from different threads; the pending list is lock-protected (same lock discipline as
  `D3D11GraphicsDevice.immediateContextLock` / `VkGraphicsDevice.graphicsQueueLock`).

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

- Pool ring: `TimestampQueryPool[framesDeep]` created on first enable from `ResourceFactory`, each `2 * RingSlotPairs` slots... simpler: ONE pool of
  `2 * RingSlotPairs` slots used as a circular allocator (slot pair = `(2k, 2k+1)`), plus a `Pending` queue of `(submitIndex, name, firstSlot, slotCount,
  debugGroupEntries[])`. `DrainCompleted` pops from the head while `TryResolveTimestamps` returns `Ready`/`Disjoint`; stops at the first `NotReady`.
- Because resets happen at `Begin()` and the circular allocator only hands out slots behind the resolve head, a command list never resets a slot that is
  still pending; if the ring is full the command list is recorded WITHOUT timestamps and `DroppedCommandLists++`.
- The per-command-list debug-group stack lives on `CommandList` (`StackList<(string name, uint slot)>`), cleared at `Begin()`.
- `CommandList.Begin()`/`End()`/`PushDebugGroup`/`PopDebugGroup` are already the public non-virtual entry points (`CommandList.cs` ~L1185-1197 and the
  backend `Begin`/`End` overrides). Hook: make the public methods call `gd.GpuTiming.OnBegin(this)` etc. before/after the `*Core` call. The D3D11 and Vulkan
  `Begin()`/`End()` are currently `public override` directly on the backend classes - they get an `internal void TimingBegin/TimingEnd` call site added, OR
  (preferred) `CommandList.Begin()/End()` become non-virtual public methods delegating to new `BeginCore/EndCore` abstracts. Decide at implementation; the
  latter is the cleaner refactor and matches the rest of `CommandList`.

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

- [ ] `ExplicitPool_TwoTimestampsAroundWorkload_T1GreaterThanT0` - pool(2); `WriteTimestamp(0)`, probe(N), `WriteTimestamp(1)`; `WaitForIdle`;
      `TryResolveTimestamps` == `Ready`, `t1 > t0`, `ticksPerSecond > 0`.
- [ ] `ExplicitPool_DoublingRepeatsRoughlyDoublesDelta` - N=8 and 2N=16 copies, delta ratio in [1.5, 3.0] (loose: driver clocks / copy engines vary).
- [ ] `ExplicitPool_NotReadyBeforeSubmit` - record both timestamps, `End()`, do NOT submit -> `NotReady`; then submit + `WaitForIdle` -> `Ready`.
- [ ] `ExplicitPool_ResolveAfterSubmitWithoutWait_NeverThrowsNeverBlocks` - call immediately after submit; assert elapsed < 5 ms and result in
      {NotReady, Ready, Disjoint}. (C3 restatement.)
- [ ] `ExplicitPool_ReuseInsideRenderPass_Throws` - write slot 0 in CL #1, submit, wait; CL #2: `SetFramebuffer` + `ClearColorTarget` (forces pass active on
      Vulkan) then `WriteTimestamp(0)` -> `VeldridException` on BOTH backends.
- [ ] `ExplicitPool_OverwriteUnresolved_IncrementsAbandonedWriteCount` - write slot 0 twice across two command lists without resolving -> count == 1.
- [ ] `Auto_CommandListMode_EverySubmitProducesOneSpan` - enable `CommandLists`; submit 3 named command lists; `WaitForIdle`; `DrainCompleted` -> 3 spans,
      names match, `Submit` indices increasing, `GpuEndTicks > GpuStartTicks`, spans non-overlapping and in submit order.
- [ ] `Auto_DebugGroupMode_NestedGroupsReportDepthAndContainment` - push A { push B { dispatch } } ; drain -> B.Depth == 1, B inside A inside CL span.
- [ ] `Auto_DebugGroupMode_InsideRenderPass_IsLegal` - push/pop around draws after `SetFramebuffer` on Vulkan; no validation error, spans produced.
- [ ] `Auto_ModeOff_ProducesNothingAndCostsNothing` - `Mode = Off`; submit; drain -> empty; `DroppedCommandLists == 0`.
- [ ] `Auto_RingFull_DropsNeverBlocks` - `RingSlotPairs = 2`; submit 10 command lists without draining -> `DroppedCommandLists == 8`, call returns promptly.
- [ ] `Features_TimestampQueries_ReportedTrueOnD3D11AndVulkan` - and `false` on the other backends when creatable in CI.

## 7. Implementation plan (incremental, each step builds + tests green)

- [ ] 7.1 `GraphicsDeviceFeatures.TimestampQueries` + NAMED arguments at all four construction sites (C5). Metal/OpenGL pass `false`.
- [ ] 7.2 `TimestampResolveResult`, `TimestampQueryPool`, `ResourceFactory.CreateTimestampQueryPool` (+ abstract `CreateTimestampQueryPoolCore`),
      `CommandList.WriteTimestamp` (+ `WriteTimestampCore`), `GraphicsDevice.TryResolveTimestamps` (+ core). Metal/OpenGL stubs (5.5).
- [ ] 7.3 Vulkan implementation (5.2 + 5.3). Feature bit from queue family.
- [ ] 7.4 D3D11 implementation (5.1 + 5.3) including disjoint window pool.
- [ ] 7.5 Probe bundle + explicit-pool tests (6.1, first six cases).
- [ ] 7.6 `CommandList.Begin/End` -> `BeginCore/EndCore` refactor (5.4) - pure refactor, no behaviour change, tests still green.
- [ ] 7.7 `GpuTiming` automatic layer (section 4, 5.4) + remaining tests.
- [ ] 7.8 Regenerate `AN.Veldrid.stableapi` (C4); update this doc's status; hand off to `95_GpuTiming.md`.

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