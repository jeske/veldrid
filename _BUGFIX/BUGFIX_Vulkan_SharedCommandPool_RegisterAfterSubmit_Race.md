# BUGFIX: Vulkan `GraphicsDevice.UpdateTexture` / `UpdateBuffer` off the submitting thread -> "An item with the same key has already been added. Key: Vulkan.VkCommandBuffer"

**Found:** 2026-11-10, AN_Monsters `Experiment_VoxelColumnTerrain --spike residency-upload --backend both` (`src/Experiment_VoxelColumnTerrain/Spikes/Spike_ResidencyUpload.cs`).
**Repro:** thread B calls `GraphicsDevice.UpdateTexture(3D texture tile)` + `UpdateBufferOutsideCommandList` in a loop (2000 iterations) while thread A records and
`SubmitCommands` command lists that copy the same resources. D3D11: PASS (0 bad texels / words). Vulkan: `ArgumentException: An item with the same key has already
been added. Key: Vulkan.VkCommandBuffer` thrown from `VkGraphicsDevice`, followed by an access violation. Single-threaded use never shows it.
**Status:** FIXED with **F2** (the submission record carries the resources to recycle) in **ArtificialNecessity.Veldrid 5.261110.12519**. Verified 2026-11-10 01:25 with the
same spike on both backends: Vulkan T1 PASS (0 / 85,184 texels, 0 / 64 words wrong, no exception; 3,605 concurrent submits), D3D11 T1 PASS. The spike's T3 (a second thread
`Map(READ)`ing a staging buffer during 1,500 ordered `UpdateBuffer -> CopyBuffer` submits) had read a NON-MONOTONIC sequence on D3D11 before the fix and is monotonic on both
backends after it (5.55 M / 134 k maps, last value 1500 / 1500) - the same wrong-completion recycling of pooled staging, seen from the Map side; the "Related" note below is
therefore closed too. Monsters' floor: `Monsters.Build.props` `ANVeldridVersion` = `[5.261110.12519,)`.

## Where

`src/Veldrid/Vk/VkGraphicsDevice.cs`:
- `UpdateBufferCore` (~L1347) and `UpdateTextureCore` (~L1386): the non-persistent / non-staging paths take a `SharedCommandPool` (`getFreeCommandPool`, L1165), record one
  copy into the pool's single `VkCommandBuffer`, and submit it through `SharedCommandPool.EndAndSubmit` (L1473).
- Bookkeeping is three `Dictionary<VkCommandBuffer, ...>` keyed by the COMMAND BUFFER HANDLE under `stagingResourcesLock`: `submittedStagingTextures`,
  `submittedStagingBuffers`, `submittedSharedCommandPools` (L90-97).
- Completion: `checkSubmittedFences` (L666; called from `SubmitCommandsCore` L622 and `WaitForIdle` L1302) -> `completeFenceSubmission` (L686) removes the three entries
  for the completed cb and recycles the staging resource and the pool (pushes a cached pool back on `sharedGraphicsCommandPools`, destroys a non-cached one).

## The bug: the completion bookkeeping is registered AFTER the submission it belongs to

`SharedCommandPool.EndAndSubmit` (L1473-1479):
```
vkEndCommandBuffer(cb);
gd.submitCommandBuffer(null, cb, ...);                      // (1) the fence is registered in submittedFences HERE
lock (gd.stagingResourcesLock) gd.submittedSharedCommandPools.Add(cb, this);   // (2) the pool is registered AFTER
```
and `UpdateBufferCore` (L1381-1382):
```
pool.EndAndSubmit(cb);                                      // (1) submitted
lock (stagingResourcesLock) submittedStagingBuffers.Add(cb, copySrcVkBuffer);   // (2) staging buffer registered AFTER
```
(`UpdateTextureCore` registers its staging TEXTURE before the submit, L1432 - correct - but its pool still goes through `EndAndSubmit`, so it has the pool half of the race.)

Between (1) and (2) another thread can run `checkSubmittedFences` (every `SubmitCommands` on the render thread does). A tiny copy completes in microseconds, so its fence is
already signalled: `completeFenceSubmission(cb)` runs, finds NO entry for `cb` in `submittedSharedCommandPools` (and / or `submittedStagingBuffers`), removes the fence, and
returns. Then (2) executes and inserts an entry that nothing will ever remove: the fence it belonged to is gone. Consequences, in order of appearance:

1. **Leak.** The pool (and the staging buffer) is never recycled. Cached pools (`shared_command_pool_count`) drain out of `sharedGraphicsCommandPools` one race at a time;
   from then on every upload creates a NON-cached `SharedCommandPool` and destroys it on completion (`vkDestroyCommandPool`).
2. **Handle reuse -> duplicate key.** `VkCommandBuffer` is a driver handle; after `vkDestroyCommandPool` the driver hands the SAME handle value to the next
   `vkAllocateCommandBuffers`. The next upload's `submittedStagingTextures.Add(cb, ...)` (L1432) or `submittedSharedCommandPools.Add(cb, this)` (L1478) now collides with the
   stale entry left by step 1 -> `ArgumentException: An item with the same key has already been added`.
3. **Use-after-free -> access violation.** If instead the stale entry is REMOVED by a LATER completion of a different submission that reused the handle, the wrong pool /
   staging resource is recycled or destroyed while its real owner is still in flight.

Keying by the raw handle is what makes (2) and (3) possible; the register-after-submit ordering is what creates the stale entries. Both are needed for the crash; the
ordering alone is already a leak. None of this is a Vulkan limitation: Vulkan permits concurrent use of distinct `VkCommandPool`s and a queue guarded by a lock (which
`graphicsQueueLock` does). It is purely the driver-side bookkeeping race.

## Fix applied (2026-11-10): carry the recycle record IN the submission ("F2")

Completion is no longer keyed by the raw `VkCommandBuffer` handle at all. `src/Veldrid/Vk/VkGraphicsDevice.cs`:

- New `readonly struct SharedPoolSubmission { SharedCommandPool Pool; VkTexture StagingTexture; VkBuffer StagingBuffer; }` stored by value in
  `FenceSubmissionInfo.SharedPool` (default / `Pool == null` for ordinary `VkCommandList` submissions).
- `submitCommandBuffer(...)` gained an overload taking a `SharedPoolSubmission`; the record is appended to `submittedFences` in the SAME `lock (submittedFencesLock)`
  as the fence, so completion can only ever observe a submission through an entry that already carries everything it must recycle. There is no register-after-submit
  window because there is no separate registration.
- `SharedCommandPool.EndAndSubmit(cb, stagingTexture, stagingBuffer)` builds the record with `this` as the pool. The 1-arg `EndAndSubmit(cb)` (layout transitions,
  L381/L403/L429) passes nulls. `UpdateBufferCore` / `UpdateTextureCore` pass their staging resource through it instead of touching a dictionary.
- `completeFenceSubmission` recycles from `fsi.SharedPool`: staging texture/buffer under `stagingResourcesLock` (now ONLY the available-list lock), pool under
  `graphicsCommandPoolLock` (cached -> push back, non-cached -> `Destroy()`).
- Deleted: `submittedStagingTextures`, `submittedStagingBuffers`, `submittedSharedCommandPools`, and the two `Debug.Assert(... .Count == 0)` in `PlatformDispose`
  (`submittedFences.Count == 0`, already asserted there, now implies the same).
- Test seam: `internal int InFlightSharedPoolSubmissionCount_ForTests` (counts `submittedFences` entries with a pool; must be 0 after `WaitForIdle`).
  `Veldrid.csproj` adds `InternalsVisibleTo("Veldrid.LifetimeTests")`.

Handle reuse is now irrelevant to correctness: a reused `VkCommandBuffer` value cannot collide with anything because nothing is looked up by it.

## Second race, found by the new concurrency test: `VkCommandList` bookkeeping vs cross-thread completion

With the shared-pool race closed, the same test (thread B uploading, thread A `Begin/End/SubmitCommands` on ONE command list) still failed on Vulkan, alternating between
`IndexOutOfRangeException` inside `Dictionary`/`List` and `ArgumentException: An item with the same key has already been added. Key: Vulkan.VkCommandBuffer` thrown from
`VkCommandList.CommandBufferSubmitted` (`src/Veldrid/Vk/VkCommandList.cs` L103). Different site, same family: `checkSubmittedFences` -> `completeFenceSubmission` ->
`VkCommandList.CommandBufferCompleted(cb)` runs on WHICHEVER thread submits (thread B's uploads included), but `VkCommandList` assumed it runs on the list owner's thread.

1. `End()` added to `submittedCommandBuffers` WITHOUT `commandBufferListLock`; `CommandBufferCompleted` does `RemoveAt` under it -> `List` corruption (the IOOBE).
2. `CommandBufferSubmitted` added to `submittedStagingInfos` WITHOUT `stagingLock`; `CommandBufferCompleted` does `Remove` under it -> `Dictionary` corruption.
3. `CommandBufferCompleted` enqueued the cb on `availableCommandBuffers` (L117) BEFORE removing its `submittedStagingInfos` entry (L129). The owner thread can
   `Begin` (dequeues that cb) -> `End` -> `Submit` -> `CommandBufferSubmitted.Add(cb)` in that window -> genuine duplicate key.

Fix applied in `VkCommandList.cs`: (1) `lock (commandBufferListLock)` around the `End()` add; (2) `lock (stagingLock)` around the `CommandBufferSubmitted` add;
(3) `CommandBufferCompleted` recycles the staging record FIRST, then returns the cb to the available queue. Pre-existing, not introduced by F2 - it was masked because
the shared-pool crash always came first.

## Threading note: why NOT just widen a lock (deadlock, not merely contention)

Lock order in the completion path is `submittedFencesLock` (held across `checkSubmittedFences`) -> `stagingResourcesLock` -> `graphicsCommandPoolLock`, and
`VkCommandList.CommandBufferCompleted` takes `stagingLock` / `commandBufferListLock` inside it too. `submitCommandBuffer` calls `checkSubmittedFences` (-> takes
`submittedFencesLock`) as its first statement. Holding `stagingResourcesLock` (or any `VkCommandList` lock) while calling into `submitCommandBuffer` therefore inverts
the order and can DEADLOCK against the render thread's `SubmitCommands`. The applied design never holds a staging/pool lock across a submit; keep it that way.

## Verification (done)

`src/Veldrid.LifetimeTests/Program.cs`, run with `cd src/Veldrid.LifetimeTests; dotnet run -c Release`:
- `Test_DeviceUpload_SingleThread_RoundTrip` - basic functionality: `UpdateTexture` (16^3 R8G8B8A8 tile, 3 passes) + `UpdateBufferOutsideCommandList` on a 256 B buffer
  (pooled staging path, `<= max_staging_buffer_size`) and a 4 KiB buffer (dispose-after-use path), each read back via a staging copy and compared word-for-word; then
  `InFlightSharedPoolSubmissionCount_ForTests == 0` after `WaitForIdle` (Vulkan).
- `Test_DeviceUpload_OffThread_WhileSubmitting_NoRaceNoLeak` - the Monsters repro in-tree: thread B does 2000 x (UpdateTexture + 2 UpdateBuffer) while thread A loops
  `Begin / CopyTexture / CopyBuffer / End / SubmitCommands` on one command list (~1.5k-7.5k submits per run). Asserts no exception on either thread, final texels/words
  equal the last upload, and 0 in-flight shared-pool submissions after idle.
- Result: 43/43 pass on Vulkan (RTX 4080) and D3D11, 4 consecutive runs. Pre-fix the concurrency test failed every run (first the shared-pool duplicate key; after F2
  alone, the `VkCommandList` IOOBE / duplicate key described above).
- Still to do by the consumer: `cd AN_Monsters/src/Experiment_VoxelColumnTerrain; dotnet run -- --spike residency-upload --backend both` against the republished
  package -> Vulkan T1 `bad texels 0 ... ok`, exit 0; then the Monsters-side workaround (render-thread-only uploads) can be relaxed if desired.

## Footnote: path not taken ("F1", register-before-submit)

The minimal alternative was to keep the three handle-keyed dictionaries and only reorder: register the pool / staging buffer under `stagingResourcesLock` BEFORE calling
`submitCommandBuffer` (`EndAndSubmit`, `UpdateBufferCore`; `UpdateTextureCore` already did this for its texture). Since completion can only run after submission, every
entry would exist before it could be looked up - no stale entries, so no leak and no handle collision in practice. Rejected because (a) it leaves correctness resting
on the driver's handle-reuse behaviour plus an ordering convention that the original code already got wrong twice, (b) a failed `vkQueueSubmit` would orphan the
pre-registered entry, and (c) it is only two lines less than F2. Also NOT a fix: widening `stagingResourcesLock` around `EndAndSubmit` - see the deadlock note above.

## Related

- Same spike, same session, D3D11 T3 (informational, NOT yet diagnosed): a second thread calling `Map(READ)` on a staging buffer while the main thread submits 1,500 ordered
  `UpdateBuffer -> CopyBuffer` command lists read the FINAL value correctly but observed a NON-MONOTONIC sequence (a later `Map` returned an older value). `D3D11GraphicsDevice.MapCore`
  takes `immediateContextLock` (L585), so the immediate context is not the race; suspects are the pooled staging path behind `CommandList.UpdateBuffer` (a pooled upload
  buffer recycled before its copy executed) or an ordering effect of `Map` vs a not-yet-executed deferred command list. Next step: repeat T3 with `WaitForIdle` between
  submits (monotonic -> a pool/ordering bug; still not -> the cross-thread `Map`). Tracked in AN_Monsters `_SPECS_RenderKit/DualContour/SPEC-BrickResidency-Thread.md`.
- Open fork item from Nov 9 (same spec, section 8): `VkCommandList.CopyBufferCore` has no pre-copy ShaderWrite -> TransferRead barrier (a same-command-list copy of a
  buffer a dispatch just wrote reads stale on Vulkan).