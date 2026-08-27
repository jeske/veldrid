# Vulkan: Record-Time Resource Refcounting (Dispose-After-Record Safety)

> **Status:** IMPLEMENTED 2026-08-26 (validation pending)  
> **Owner:** `C:\PROJECTS\AN_Veldrid` (our fork)  
> **Driven by:** `C:\PROJECTS\AN_FluidUI\_BUGFIX\SilkyNvg_DeferredTextureDeletion_CommandQueueLifetime.md`  
> **Affected backend:** Vulkan only (`src/Veldrid/Vk/VkCommandList.cs`)

## Background: per-backend disposal contract

| Backend | Dispose-after-record safety | Mechanism |
|---|---|---|
| D3D11 | SAFE | COM refcounts: deferred context / driver hold refs on recorded resources |
| Metal | SAFE | `commandQueue.commandBuffer()` = retained-references variant; encode-time ObjC retain until GPU completion |
| Vulkan | Was **conditionally safe** — see gaps below | `ResourceRefCount` + `StagingResourceInfo.Resources` tracking |

## What Vulkan already did (initial audit correction)

An early audit wrongly concluded draw-bound ResourceSets were untracked (a
`search_files` per-file match cap hid call sites). In fact `VkCommandList` already
tracked, per recording, in `currentStagingInfo.Resources` (a `HashSet<ResourceRefCount>`):

- staging buffers, copy/resolve src+dst, GenerateMipmaps textures
- indirect draw/dispatch argument buffers
- vertex + index buffers, pipelines
- framebuffers, swapchains
- **ResourceSets and their contained resources** (`flushNewResourceSets`:
  `vkSet.RefCount` + `vkSet.RefCounts`)

`CommandBufferSubmitted` incremented every tracked refcount; `recycleStagingInfo`
(called on GPU completion) decremented them.

## The two REAL gaps (both fixed)

### Gap 1 — the record→submit window was unprotected

Refs were **added at record time but incremented only at submission**. A consumer that
records draws referencing a texture and then calls `texture.Dispose()` *before the host
submits* drops the refcount to 0 → `vkDestroyImage` immediately → the later
`vkQueueSubmit` executes against destroyed handles. This is exactly the SilkyNvg
"physically dispose deferred-deleted textures at end-of-Flush()" pattern: SilkyNvg's
Flush records into the CommandList; the host submits later.

### Gap 2 — abandoned recordings over-decremented

`Begin()` after End-without-submit called `recycleStagingInfo(currentStagingInfo)`,
which **decremented refcounts that were never incremented** (submission never happened).
That destroys live resources the application still owns.

## Fix — one invariant: membership in `Resources` holds exactly one ref

- New helper `addStagingResourceRef(ResourceRefCount)`:
  `if (currentStagingInfo.Resources.Add(rrc)) rrc.Increment();` — increment at RECORD
  time, deduped per recording by the existing HashSet.
- All 17 former `currentStagingInfo.Resources.Add(...)` call sites now use the helper.
- `CommandBufferSubmitted`: the per-resource `Increment()` loop is REMOVED (the
  CommandList's own `RefCount.Increment()` remains).
- `recycleStagingInfo` still decrements once per tracked resource — now balanced on
  BOTH paths (GPU completion of a submitted recording, or abandonment via `Begin()`).
- `disposeCore`: recycles a live `currentStagingInfo` so disposing a CommandList with a
  recorded-but-unsubmitted recording releases its refs instead of leaking them.

### Resulting contract (all three primary backends)

Once a command referencing a resource has been **recorded**, `Dispose()` on that
resource (or a resource set containing it) is safe: physical destruction is deferred
until the recording is either GPU-completed or abandoned. Consumers (SilkyNvg) need no
fence tracking or host submission handshake.

## Out of scope

- OpenGL backend (executor-thread lifetime model; separate audit in the SilkyNvg plan).
- GLES/Android specifics.

## Validation

- [x] `dotnet build src/Veldrid/Veldrid.csproj` — 0 errors.
- [x] `src/Veldrid.LifetimeTests` (console harness, in Veldrid.sln) — 11/11 PASS on
      RTX 4080, 2026-08-26: Vulkan runs STRICT deferred-destruction assertions via
      `Texture.IsDisposed` (VkTexture.destroyed); D3D11 runs the same sequences as
      no-crash smoke. CopyTexture commands exercise the same `addStagingResourceRef`
      path as draw-bound ResourceSets (no shaders needed).
  - [x] Gap 1: record → Dispose → submit → complete: destruction deferred until GPU completion.
  - [x] Gap 2: End-without-submit → Begin(): owned resources survive; owner Dispose destroys.
  - [x] CommandList.Dispose with unsubmitted recording: refs released, no leak.
  - [x] 50-cycle record/dispose/submit churn: every texture eventually destroyed (no retirement leak).
- [ ] Vulkan VALIDATION-LAYER run (GraphicsDeviceOptions debug:true) of the lifetime tests + a draw-based scenario (SilkyNvg TextureLifetimeTest --vulkan).
- [ ] Smoke test SilkyNvg Veldrid samples on D3D11 + Vulkan.