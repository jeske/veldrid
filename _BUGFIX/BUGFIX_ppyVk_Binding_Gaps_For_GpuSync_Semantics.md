# BUGFIX - STATUS: UNRESOLVED (binding gap, parked) - ppy.Vk 1.0.26 lacks the Vulkan 1.1/1.2 entry points needed for GPU-side ordering without a CPU stall

> **STATUS: UNRESOLVED.** Nothing is broken today; this records what the Vulkan binding this fork consumes (`ppy.Vk` 1.0.26, NuGet, `vk.dll` dated
> 2024-04-16, Vulkan ~1.0/1.1 surface) does NOT expose, and therefore what would have to be added - most likely by forking it into `AN_VulkanBindings`
> (user-approved in principle, deferred until necessary) - before the Vulkan backend can implement the deeper fence/semaphore semantics that
> `_TASKS/GpuOrderingBarrier_vs_WaitForGpuIdle.md` section 7 describes. Decision to proceed: not taken.

Filed Nov 10 2026 at the user's request after the `GpuOrderingBarrier()` / `WaitForGpuIdle()` split landed (commit `efb410d3`).
Related: `_TASKS/GpuTimestampQueries.md` (E1: host query reset also absent), `_BUGFIX/BUGFIX_D3D11_MultiDevice_FinishCommandList_AccessViolation.md`.

## 1. Why this matters

`GraphicsDevice.GpuOrderingBarrier()` promises GPU-side ORDERING (work submitted before is complete/visible to work submitted after, and to a
following `Map(READ)`) and makes no promise that the CPU waited. On Direct3D11 that is free (implicit sync). On Vulkan the backend currently
delivers it with `vkQueueWaitIdle` - a full CPU<->GPU drain - because that was what upstream Veldrid's `WaitForIdle()` did. The split made the
intent explicit; the follow-up is to make the Vulkan implementation as cheap as the verb allows. Doing that well needs API the binding does not have.

## 2. Inventory - what ppy.Vk 1.0.26 has and lacks (verified Nov 10 2026 by scanning `vk.dll` for the symbol names)

### Present (Vulkan 1.0 core, enough for the FIRST step)

| symbol | use for us |
|---|---|
| `vkCreateFence` / `vkWaitForFences` / `vkGetFenceStatus` / `vkResetFences` | every Veldrid Vulkan submission already carries a `VkFence` (staging recycling); a targeted wait on the LAST submission instead of a queue drain is possible today |
| `vkCreateSemaphore` (binary) / `VkSubmitInfo` wait+signal arrays | chain submission N -> N+1 on the GPU with no CPU involvement (binary semaphores: one wait per signal, must be re-signalled each use) |
| `vkCmdPipelineBarrier`, `VkBufferMemoryBarrier`, `VkImageMemoryBarrier` | intra-queue memory visibility between command buffers - the thing `vkQueueWaitIdle` currently papers over for buffers |
| `vkCreateEvent` / `vkCmdSetEvent` / `vkCmdWaitEvents` | fine-grained intra-queue split barriers (not needed for the plan, but available) |
| `vkGetSemaphoreWin32HandleKHR` | external semaphore export (present, oddly, without its create-info struct - see below) |

### ABSENT (what "deeper semantics" needs)

| symbol | Vulkan version / extension | what it would give us |
|---|---|---|
| `VkSemaphoreTypeCreateInfo`, `VkSemaphoreType`, `VkTimelineSemaphoreSubmitInfo`, `vkWaitSemaphores`, `vkSignalSemaphore`, `vkGetSemaphoreCounterValue` | **1.2 core** / `VK_KHR_timeline_semaphore` (1.1) | **Timeline semaphores**: one monotonically increasing counter per queue; a submission signals value N, any later submission or the HOST waits for >= N. Replaces per-submission fences AND binary semaphore chains with a single object, supports multi-queue (compute/transfer) ordering, and lets `GpuOrderingBarrier()` become "next submit waits for value N" (no CPU) while `Map(READ)` becomes `vkWaitSemaphores(N)` on exactly the producing submission. This is THE missing piece. |
| `vkGetPhysicalDeviceFeatures2`, `VkPhysicalDeviceFeatures2`, `VkPhysicalDeviceVulkan11Features`, `VkPhysicalDeviceVulkan12Features`, `VkPhysicalDeviceTimelineSemaphoreFeatures` | 1.1 core | required to QUERY and ENABLE timeline semaphores (and any other 1.1+/1.2 feature) at device creation - without the `pNext`-chained features structs the device cannot opt in |
| `vkGetPhysicalDeviceProperties2` | 1.1 core | needed for `VkPhysicalDeviceTimelineSemaphoreProperties.maxTimelineSemaphoreValueDifference` and for driver/vendor IDs (`VkPhysicalDeviceDriverProperties`) |
| `vkEnumerateInstanceVersion` | 1.1 core | know whether 1.2 is even available before asking for it (today the backend requests a 1.0/1.1 instance) |
| `vkQueueSubmit2`, `VkSemaphoreSubmitInfo`, `vkCmdPipelineBarrier2`, `VkMemoryBarrier2`, `VkBufferMemoryBarrier2` | **1.3 core** / `VK_KHR_synchronization2` | the modern sync API: per-semaphore stage masks, 64-bit stage/access flags, simpler barrier expression. Not required (1.2 timeline semaphores + 1.0 barriers suffice) but the natural target if the binding is being extended anyway |
| `vkResetQueryPool` | 1.2 core / `VK_EXT_host_query_reset` | host-side query reset; the timestamp work (spec E1) designed around its absence (reset at `Begin()`); would simplify the pool state machine |
| `vkGetCalibratedTimestampsEXT`, `VkCalibratedTimestampInfoEXT` | `VK_EXT_calibrated_timestamps` | GPU<->host clock correlation for a combined CPU+GPU timeline in the kit profiler (spec `GpuTimestampQueries.md` section 8); D3D11 has no equivalent, so this is Vulkan-only polish |
| `VkExportSemaphoreCreateInfo` | `VK_KHR_external_semaphore` | the create-info half of the export path whose `Win32Handle` getter IS present; irrelevant to us unless D3D/Vulkan interop ever comes up |

## 3. What would be done with it (the plan this unblocks; none of it is started)

Prerequisite regardless of binding: a **buffer-barrier audit** of `VkCommandList`. `vkQueueWaitIdle` currently also hides missing
`VkBufferMemoryBarrier`s between command buffers (`CopyBufferCore` has no pre-copy barrier - on record). If the ordering verb stops draining the
queue before that audit, sites relying on the accidental insurance become GPU races. Images are covered by Veldrid's layout transitions.

Then, in order of binding requirement:

1. **No binding change (1.0):** `GpuOrderingBarrierCore` records "ordering requested" and resolves it lazily - `vkWaitForFences` on the LAST
   submission's existing fence only when a `Map(READ)` or a destroy actually follows; otherwise emit nothing. Readback cost is inherent (the GPU
   must finish that work); the win is every reuse/destroy site that needs no CPU wait at all.
2. **1.2 / timeline semaphores (binding change):** one timeline per device queue; `SubmitCommands` signals `++counter`; `GpuOrderingBarrier()`
   stores `counter` and the NEXT `SubmitCommands` waits for it GPU-side; `Map(READ)` and `WaitForFence` use `vkWaitSemaphores`; `Fence` becomes a
   (semaphore, value) pair; the per-submission `VkFence` objects and the staging-recycle bookkeeping collapse onto the one counter. Multi-queue
   (async compute / transfer) becomes expressible for the first time.
3. **Optional 1.3 / synchronization2:** rewrite barriers on `vkCmdPipelineBarrier2` for clarity; not functionally required.

## 4. How the binding would be extended (when it is decided)

- `ppy.Vk` is ppy's maintained fork of `mellinoe/vk` (generated from `vk.xml`); the package is 2024-04 and tracks Vulkan ~1.1 headers. Options:
  (a) fork into `C:\PROJECTS\AN_VulkanBindings`, regenerate from a current `vk.xml` with 1.2/1.3 core + the extensions above (the generator is in
  the mellinoe/vk repo); (b) hand-write the ~25 missing structs/functions as `partial class VulkanNative` additions loaded through the existing
  `vkGetInstanceProcAddr` / `vkGetDeviceProcAddr` pattern (the fork already does this for `vkCreateDebugReportCallbackEXT` in
  `VkGraphicsDevice.EnableDebugCallback`) - fastest, no regeneration, but a second source of truth. (b) is enough for step 2.
- Either way `Veldrid.csproj` line 29 (`PackageReference ppy.Vk 1.0.26`) is the single switch; `src/Veldrid/Vk/*` is the only consumer.
- Device creation must then request a 1.2 instance/device when available, enable `timelineSemaphore` via `VkPhysicalDeviceVulkan12Features` in the
  `pNext` chain, and fall back to the 1.0 path (step 1) on drivers that lack it (`Features`-style capability bit, mirroring `TimestampQueries`).

## 5. Explicitly NOT a reason to do this now

- The D3D11 multi-device AccessViolation (`BUGFIX_D3D11_MultiDevice_FinishCommandList_AccessViolation.md`) is a D3D11 driver issue; nothing here helps it.
- The `GpuOrderingBarrier`/`WaitForGpuIdle` split already landed with zero behaviour change on Vulkan; clients are correct today, just paying a
  CPU drain per ordering point that the verb no longer promises. Measure that cost in a real frame (AN_Monsters, FluidUI) before investing in
  steps 2-3; step 1 needs no binding work and may capture most of it.