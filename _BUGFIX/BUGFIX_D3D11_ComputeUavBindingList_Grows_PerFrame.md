# BUGFIX: D3D11 - a per-frame compute dispatch makes every later draw slower, linearly in frames (1 of 2)

## Status: FIXED in AN_Veldrid (Nov 9 2026, on top of `4d0c393c`)

- `src/Veldrid/D3D11/D3D11CommandList.cs`
  - `resetManagedState()` now `Clear()`s `boundComputeUavBuffers` and `boundOmuavBuffers` with every other binding cache (clear only, no
    `CSUnset`/`OMUnset`: every caller has already reset the device context - `ClearState()` on `Begin`/`Reset`, `FinishCommandList(false)`
    on `End`).
  - `trackBoundUavBuffer` replaces the existing entry for the same `(stage, slot)` instead of appending, so each mirror is bounded by the
    UAV slot count (8) within one command list. This also fixes a latent correctness fault the note below did not call out: a stale
    `(oldBuffer, slot)` entry survived a rebind of that slot, so a later vertex/index bind of `oldBuffer` would `CSUnset`/`OMUnset` the slot
    now holding a DIFFERENT buffer.
  - Two `internal` test seams (visible via `InternalsVisibleTo`): `DebugTrackedUavBufferCount(bool compute)` and
    `DebugTrackUavBufferForTests(DeviceBuffer, int slot, bool compute)` (calls the same private tracking path a resource-set bind does).
- `src/Veldrid.ContractTests/D3D11UavBufferMirrorTests.cs` (xunit, skips without a D3D11 device): 1,000 simulated frames x 4 UAVs leave
  4 entries, not 4,000; rebinding a slot replaces its entry; compute and OM stages are separate; `End` and `Begin` both leave 0. The seam is
  used because these tests have no compiled compute `.vdshader` bundle (the V9 contract forbids building a compute pipeline without one).
- Other `D3D11CommandList` collections checked for the same fault: `boundSRVs`/`boundUaVs` are cleared in `resetManagedState`;
  `submittedStagingBuffers`/`referencedSwapchains` are cleared in `OnCompleted`; `availableStagingBuffers` is a bounded recycle pool. None grow.
- Still to do by the reporter: re-run the AN_Monsters repro in "How to verify" against this build and record the `shadow+light` figure.

---

Filed Nov 9 2026 from AN_Monsters (`src/Experiment_VoxelColumnTerrain`, the column-voxel terrain bench), commit `704085c`. Companion note (2 of 2):
`BUGFIX_Vulkan_CopyBuffer_NoPreCopyBarrier.md` - the Vulkan `CopyBufferCore` barrier gap found the same day.

## Symptom

An app that records ONE small compute dispatch every frame (25 workgroups, 4 UAVs bound) on the **D3D11** backend decays from ~60 fps to ~30 fps over about 40 s
at a completely constant scene, then keeps decaying. Removing the dispatch (leaving every buffer update in place) makes the frame flat. The extra time does NOT appear
in the dispatch's own recording; it appears in the CPU recording time of the **later passes in the frame** - the one with the most vertex / index / SRV binds
(the shadow pass) grows the most. Nothing on the GPU grows.

Measured (RTX 4080, D3D11, bench stats line `record [gbuf .. brick .. shadow+light ..]`):

| time | fps | shadow+light pass record (CPU ms) | drawn bricks |
|---|---|---|---|
| t+0 s | 66 | 0.5 | 1567 |
| t+12 s | 69 | 7.2 | 1567 |
| t+24 s | 48 | 15.6 | 1567 |
| t+36 s | 37 | 21.2 | 1567 |

Bisection (one variable per run, all at the constant scene): readback copies + `Map` disabled -> still grows; fragment-stage UAV atomics disabled -> still grows;
the dispatch's two `UpdateBuffer`s kept but `Dispatch` skipped -> **flat**; `Dispatch` kept, counts-zero `UpdateBuffer` skipped -> grows. The dispatch is the cause.

## Root cause (read in the fork, `src/Veldrid/D3D11/D3D11CommandList.cs`)

Two lists track which BUFFERS are bound as UAVs, so a later bind of the same buffer as a vertex / index / shader-resource view can unset the UAV first (D3D11 forbids
a resource bound as UAV and SRV at once):

```csharp
// line 58-60
private const int max_ua_vs = 8;
private readonly List<(DeviceBuffer, int)> boundComputeUavBuffers = new List<(DeviceBuffer, int)>(max_ua_vs);
private readonly List<(DeviceBuffer, int)> boundOmuavBuffers = new List<(DeviceBuffer, int)>(max_ua_vs);

// line 939-943: called from bindUnorderedAccessView for every buffer UAV the pipeline's resource set binds
private void trackBoundUavBuffer(DeviceBuffer buffer, int slot, bool compute)
{
    var list = compute ? boundComputeUavBuffers : boundOmuavBuffers;
    list.Add((buffer, slot));                                   // <- APPEND, unconditional, no de-duplication
}

// line 951-969: the ONLY removal - a linear scan; called on every SetVertexBuffer (1175), SetIndexBuffer (1059) and buffer-SRV bind (772)
private void unbindUavBufferIndividual(DeviceBuffer buffer, bool compute)
{
    var list = compute ? boundComputeUavBuffers : boundOmuavBuffers;
    for (int i = 0; i < list.Count; i++)
        if (list[i].Item1 == buffer) { ... CSUnsetUnorderedAccessView / OMUnsetUnorderedAccessView ...; list.RemoveAt(i); i -= 1; }
}
```

`resetManagedState()` (lines 429-487, run at `End` via `FinishCommandList` and at `Begin` / `Reset` / `clearState`) clears EVERY other binding cache - vertex
bindings, resource sets, `boundSRVs`, `boundUaVs` (the TEXTURE UAV dictionary) - but **never touches `boundComputeUavBuffers` or `boundOmuavBuffers`**.

Consequences:
1. Every frame's compute dispatch appends its N buffer UAVs (4 in our case) to `boundComputeUavBuffers`. Nothing ever removes them unless that exact buffer is later
   bound as a vertex / index / SRV buffer in the same list - ours never are (they are compute-only). The list grows by 4 per frame, forever (also the entries are
   duplicates of the same 4 buffers).
2. Every `SetVertexBuffer`, `SetIndexBuffer` and buffer-SRV bind in EVERY later pass of EVERY later frame runs `unbindUavBuffer`, i.e. two linear scans of lists
   that are now thousands long. The shadow pass (5 cascades x ~450 chunks x index + vertex binds) pays the most: O(frames x draws).
3. It is invisible until an app dispatches compute every frame on D3D11. `Experiment_ViewCards`' `ImpostorForest` (cull compute each frame) has the same latent
   decay; the Vulkan backend tracks bindings differently and does not show it.

## Fix (one place)

In `resetManagedState()`, unset and clear both lists. The device context is already `ClearState()`d on the `clearState` / `Reset` paths, and `FinishCommandList`
ends the deferred context's state; the managed mirror must match:

```csharp
// resetManagedState(), after boundUaVs.Clear();
boundComputeUavBuffers.Clear();
boundOmuavBuffers.Clear();
```

Two optional hardenings while there:
- `trackBoundUavBuffer`: replace an existing entry for the same `(compute, slot)` instead of appending (a slot holds one UAV), so the list is bounded by the UAV
  slot count (8) even within one command list that re-binds the same set many times.
- `unbindUavBuffer` is also called for buffers that were never UAVs (every vertex/index bind); with the lists bounded that scan is 0-8 compares, fine.

## How to verify

- Repro before: AN_Monsters `cmd\tower-test.cmd --exit-after 40` (or any bench run with `--lod-levels 5 --far-field split 48`): the `record [... shadow+light N ...]`
  figure in the `[FrameStats]` log line climbs ~0.5 ms/s at a constant `drawn` count. With the env toggle `VOXEL_BRICK_VERDICT=nodispatch` it stays flat.
- After the fix: the same run holds `shadow+light` flat (~6 ms at that pose) for the whole 40 s with the dispatch enabled.
- A unit-level check in the fork: record a command list with a compute dispatch binding 4 buffer UAVs, `End`, `Begin` again, and assert the two lists are empty
  (expose a `Debug`-only count, or test through the public surface by timing 10,000 `SetVertexBuffer` calls after 1,000 such frames).

## Also observed today (not this bug, for the record)

- D3D11 `Fence`s are set when `SubmitCommands` returns (ManualResetEvent), not on GPU completion; a readback must age its staging ring by frames, not fences, or
  `Map(READ)` blocks for the whole GPU frame (we gate takes at >= 2 frames).
- `StagingBufferPoolTelemetry` was checked during the bisection and is NOT involved (30 buffers created, then flat).