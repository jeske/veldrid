# TASK: Split `WaitForIdle()` into `GpuOrderingBarrier()` (cheap GPU ordering) and `WaitForGpuIdle()` (true CPU stall)

Filed Nov 10 2026 (user + review, during `_TASKS/GpuTimestampQueries.md`). Status: SPEC - awaiting go. Companion finding:
`_TASKS/GpuTimestampQueries.md` section 8 and `_BUGFIX/BUGFIX_D3D11_MultiDevice_FinishCommandList_AccessViolation.md`.

Naming: `GpuOrderingBarrier()` is the working name ("barrier" = GPU-side ordering, never a CPU wait, matching `vkCmdPipelineBarrier` / D3D12
barriers; it deliberately avoids `Fence`, which in Veldrid/D3D12/Vulkan is a CPU-waitable OBJECT - `CreateFence`, `SubmitCommands(cl, fence)`,
`WaitForFence`). The user's first proposal was `GpuOrderingFence()`; if preferred, it is a search-and-replace at implementation time.

## 1. The problem - `WaitForIdle()` names a CPU stall but is used as a GPU ordering point

`GraphicsDevice.WaitForIdle()` is documented as "blocks until all submitted CommandLists have fully completed". Reality per backend today:

| backend | `WaitForIdleCore` | what callers get |
|---|---|---|
| Vulkan | `vkQueueWaitIdle` | a real CPU stall until the queue drains |
| Metal | wait on the last committed command buffer | a real CPU stall |
| D3D11 | **empty method** (inherited from upstream Veldrid) | nothing - returns immediately |
| OpenGL | hand-off wait on the GL execution thread | CPU-thread sync with the GL thread, not a GPU wait |

And `D3D11Fence.Set()` is called right after `ExecuteCommandList` (`D3D11GraphicsDevice.SubmitCommandsCore`), so on D3D11 `WaitForFence` is equally
hollow: it signals when the CPU handed the list to the runtime, not when the GPU finished it.

Why nothing is broken: D3D11 is an implicitly synchronised API - the runtime tracks every resource's pending GPU use and inserts the waits
(`Map(READ)` blocks until the copy landed, `Map(WRITE_DISCARD)` renames, destruction is deferred). Vulkan has none of that, so Veldrid's Vulkan
backend NEEDS a serialisation point before readbacks, resource reuse and teardown, and `vkQueueWaitIdle` is the crude way it gets one.

**The insight (user, Nov 10):** the overwhelming majority of `WaitForIdle()` call sites exist to guarantee GPU-side ORDERING for Vulkan's benefit,
not because the CPU needs to stall. On D3D11 that ordering is free, so the no-op is CORRECT for what those call sites mean. Making D3D11's
`WaitForIdle` a real stall (the obvious "fix") would insert a hard CPU<->GPU bubble at every readback prelude and resource-recycle point in
AN_Monsters and AN_FluidUI on D3D11 - a regression dressed as a fix. REJECTED.

The only callers that genuinely need the CPU to wait for GPU completion: timing code (`Submit; WaitForIdle(); stopwatch.Stop()` - meaningless on
D3D11 today), reading GPU queries without polling, device/swapchain teardown, and tests that assert on GPU completion.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | Two verbs with different costs, visible at the call site. No boolean parameter: a flag that toggles "flush" vs "wait" is the kind whose meaning is lost in a month. |
| D2 | `GpuOrderingBarrier()` = the CHEAPEST thing each backend can do to guarantee that GPU work submitted before the call is complete and visible to GPU work submitted after it and to subsequent `Map(READ)`. Never promises the CPU waited. D3D11: nothing. Vulkan: today `vkQueueWaitIdle` (unchanged), later a GPU-side fence/semaphore. Metal: command-buffer ordering (today: the existing wait, unchanged). OpenGL: today's execution-thread hand-off. |
| D3 | `WaitForGpuIdle()` = block the CALLING CPU THREAD until every GPU command submitted so far has finished executing, on EVERY backend. D3D11 gets a real implementation (`Flush` + `D3D11_QUERY_EVENT` on the immediate context, polled to `S_OK`). |
| D4 | `WaitForIdle()` stays, `[Obsolete]`, as an alias of `GpuOrderingBarrier()`: byte-for-byte today's behaviour on every backend, so NOTHING gets slower by this change. The obsolete message says which verb to pick. Removed in a later cut once clients are migrated. |
| D5 | `D3D11Fence` becomes GPU-accurate: one `D3D11_QUERY_EVENT` per fence, `End()`ed on the immediate context after `ExecuteCommandList`; `Signaled` / `WaitForFence` poll `GetData`. Only code that explicitly asked for a fence pays for this. |
| D6 | Vulkan's `GpuOrderingBarrier` keeps `vkQueueWaitIdle` in THIS cut (zero behaviour change). Replacing it with a timeline semaphore / fence-wait-on-next-submit is the follow-up this rename makes possible; filed as section 7 open item, not done here. |
| D7 | Internal fork call sites that destroy or recycle native objects (device `Dispose`, Vulkan swapchain resize, Metal `PlatformDispose`) use `WaitForGpuIdle()` - they need the GPU finished, not just ordered. |
| D8 | OpenGL: both verbs map to the existing execution-thread hand-off (GL has no multi-queue ordering problem and `glFinish` is already what the thread does on demand). No change. |

## 3. Public API

```csharp
public abstract partial class GraphicsDevice
{
    /// <summary>
    ///     Guarantees that GPU work submitted before this call is complete and visible to GPU work submitted after it and to subsequent
    ///     <see cref="Map(MappableResource, MapMode)"/> reads. The CHEAPEST thing each backend can do - this is a GPU-side ORDERING point and
    ///     makes NO promise that the calling thread waited: Direct3D11 does nothing (the runtime orders implicitly); Vulkan currently drains the
    ///     queue (vkQueueWaitIdle); Metal/OpenGL use their existing ordering. Use this before readbacks and before reusing/destroying resources.
    ///     For a real CPU wait (timing, teardown, tests) use <see cref="WaitForGpuIdle"/>.
    /// </summary>
    public void GpuOrderingBarrier();                       // -> GpuOrderingBarrierCore()

    /// <summary>
    ///     Blocks the calling CPU thread until EVERY GPU command submitted so far has finished executing, on every backend (Direct3D11:
    ///     Flush + event query). Costly by definition - a full CPU<->GPU bubble. Legitimate uses: timing around a submit, reading GPU queries
    ///     without polling, device/swapchain teardown, tests. Grep for it in a perf review.
    /// </summary>
    public void WaitForGpuIdle();                           // -> WaitForGpuIdleCore()

    [Obsolete("Say what you mean: GpuOrderingBarrier() (GPU ordering, cheap - what almost every caller wants) or WaitForGpuIdle() (real CPU stall). WaitForIdle() is GpuOrderingBarrier() and will be removed.")]
    public void WaitForIdle() => GpuOrderingBarrier();
}
```

Backend cores (`private protected abstract`): `GpuOrderingBarrierCore()`, `WaitForGpuIdleCore()`. The existing `WaitForIdleCore()` is renamed to
`GpuOrderingBarrierCore()` on every backend (identical bodies) and each backend adds `WaitForGpuIdleCore()`:

| backend | `GpuOrderingBarrierCore` (= old `WaitForIdleCore`, unchanged) | `WaitForGpuIdleCore` (new) |
|---|---|---|
| D3D11 | empty | `lock(immediateContextLock) { immediateContext.Flush(); End(eventQuery); spin GetData(eventQuery) until S_OK (SpinWait then Sleep(0)) }`; one cached `ID3D11Query(EVENT)` per device |
| Vulkan | `vkQueueWaitIdle` | `vkQueueWaitIdle` (same; already a true wait) |
| Metal | wait last committed cb | same |
| OpenGL | execution-thread hand-off | same |

`D3D11Fence` (D5): `ID3D11Query(EVENT) Query` field; `SubmitCommandsCore` -> after `ExecuteCommandList`, `immediateContext.End(fence.Query)`;
`Signaled => GetData(Query, DoNotFlush) == S_OK`; `WaitForFence(fence, timeout)` -> `GetData` with flush allowed, poll until `S_OK` or timeout;
`Reset()` -> nothing to do on D3D11 beyond marking (an EVENT query re-`End`ed later is the canonical cycle). The `ManualResetEvent` goes away.

## 4. Call-site migration (the bulk of the work)

Rule of thumb per call site - ask "what breaks if the CPU does NOT wait here?":
- Nothing on D3D11 (the next thing is a `Map(READ)`, a resource reuse, a resize of our own textures) -> **`GpuOrderingBarrier()`**.
- A measurement is wrong, a query is read without polling, a native object is destroyed under the GPU -> **`WaitForGpuIdle()`**.

### 4.1 Fork (`src/`, 33 sites)

| site | category | new verb |
|---|---|---|
| `GraphicsDevice.Dispose()` | teardown | `WaitForGpuIdle` |
| `MtlGraphicsDevice.PlatformDispose()` | teardown | `WaitForGpuIdle` |
| `VkSwapchain` resize (`gd.WaitForIdle()` before destroying the old swapchain) | teardown of a native object | `WaitForGpuIdle` |
| `OpenGLGraphicsDevice.SwapBuffersCore` and its internal `executionThread.WaitForIdle()` | GL-thread hand-off, backend-internal | unchanged (internal name may stay) |
| ContractTests `Submit; WaitForIdle; Map(READ)` (ExplicitUniformStorage, ImGuiShapeRender, SharedUniformStorage, UniformArenaRender, UpdateBufferOutsideCommandListPermission) | readback ordering | `GpuOrderingBarrier` |
| `TimestampQueryTests` / `GpuTimingAutoTests` `WaitForIdle` before resolving | query readback - wants completion | `WaitForGpuIdle`; `ResolveWithRetry` / `DrainWithRetry` then assert `Ready` on the FIRST call on D3D11 too (they become regression detectors for D3), keep the poll only as a timeout guard |
| `Veldrid.LifetimeTests` (7 sites: "triggers submitted-fence processing", readback checks, teardown) | completion semantics | `WaitForGpuIdle` |
| `Veldrid.ImGui.SDL2.Test` shutdown | teardown | `WaitForGpuIdle` |

### 4.2 Clients (filed as obligations in their own repos; the obsolete alias keeps them compiling and unchanged until then)

| client | sites | expected split |
|---|---|---|
| AN_Monsters `src` | 24 | readback preludes (`DepthPickReadback`, spikes' `CopyBuffer/CopyTexture ... Map`) -> `GpuOrderingBarrier`; `Spike_*` timing (`submitClock` around submits, `Submit; WaitForIdle; elapsed`) -> `WaitForGpuIdle` (their D3D11 numbers are meaningless today); `StaticDeviceBufferUpload` / `TerrainMaterialLoader` one-shot uploads -> `GpuOrderingBarrier`; shutdown -> `WaitForGpuIdle` |
| AN_FluidUI `src` | 24 | `CompositorThread.cs:588` `coordinator.WaitForIdle(); // GPU finishes composite` -> `GpuOrderingBarrier` (the comment is false on D3D11 today and the hand-off is implicitly ordered; verify the pointer swap only needs ordering); readback copy -> `GpuOrderingBarrier`; shutdown/resize -> `WaitForGpuIdle`; `EmbeddedGraphicsCoordinator.WaitForIdle()` wrapper -> expose both verbs |
| AN_ArcaneSiege | 0 | - |

## 5. Tests (`Veldrid.ContractTests`, D3D11 + Vulkan)

- [ ] `WaitForGpuIdle_ThenTimestampsAreReadyOnFirstResolve` - submit heavy work with two timestamps, `WaitForGpuIdle()`, `TryResolveTimestamps` == `Ready` with NO retry (D3D11 fails this today).
- [ ] `GpuOrderingBarrier_ReadbackObservesPriorWork` - copy to staging, `GpuOrderingBarrier()`, `Map(READ)` sees the data (both backends; D3D11 proves the implicit path).
- [ ] `Fence_NotSignaledImmediatelyAfterHeavySubmit_SignaledAfterWaitForFence` - D3D11 currently signals immediately; must not.
- [ ] `Fence_WaitForFenceTimeout_ReturnsFalseWhileGpuBusy` - 1 ns timeout right after a heavy submit -> false; then `WaitForFence(fence)` -> true.
- [ ] `WaitForIdle_Obsolete_IsGpuOrderingBarrier` - compile-time `#pragma warning disable CS0618` test that the alias still runs (behaviour unchanged).

## 6. Implementation plan

- [ ] 6.1 Rename `WaitForIdleCore` -> `GpuOrderingBarrierCore` (4 backends, bodies untouched); add `GpuOrderingBarrier()`, `WaitForGpuIdle()` + abstract `WaitForGpuIdleCore()`; `[Obsolete] WaitForIdle()` alias. Build green, ContractTests green (no behaviour change yet).
- [ ] 6.2 D3D11 `WaitForGpuIdleCore` (event query). Vulkan/Metal/OpenGL `WaitForGpuIdleCore` = their barrier core.
- [ ] 6.3 `D3D11Fence` on an EVENT query (D5).
- [ ] 6.4 Fork call-site migration (4.1) incl. the timestamp tests' retry loops becoming first-call asserts.
- [ ] 6.5 Tests (section 5). Full suite x5 serial.
- [ ] 6.6 Regenerate `AN.Veldrid.stableapi`; docs; publish to LocalNuGet.
- [ ] 6.7 File client obligations: `AN_Monsters/_SPECS_RenderKit/95_GpuTiming.md` (append) and `AN_FluidUI/_TASKS/GpuTiming_GameCompositor.md` (append) with the 4.2 tables.

## 7. Open questions / follow-ups

- Vulkan `GpuOrderingBarrierCore` without a CPU stall (D6): track the last submitted fence and have the NEXT `SubmitCommands` wait on it
  GPU-side (semaphore), with `Map(READ)` doing a targeted `vkWaitForFences` on the specific submission instead of the whole queue. This is the
  real payoff of the split for Vulkan; separate task once the rename has landed and the call sites say what they mean.
- Should `GpuOrderingBarrier()` be allowed inside the GameCompositor's `submitLock` hold? Today `WaitForIdle()` is, and on Vulkan it stalls the
  compositor thread while holding the lock; unchanged by this task, but worth a look when the compositor adopts the two verbs.
- Naming: `GpuOrderingBarrier` vs the user's `GpuOrderingFence` - see header.