# BUGFIX: GPU Buffer Update-Path Compatibility — device-level writes versus command-recorded transfers

**Status:** DEFERRED by user decision (2026-10-31). Do not implement this compatibility project as part of the current Silky migration. Historical implementation instructions below are retained, not an instruction to resume now.
**Owner:** AN_Veldrid. Moved from AN_FluidUI/_BUGFIX/450_GPUBuffer_Safety.md on 2026-10-31; FluidUI discovered the symptom but the defect is in Veldrid.
**Original consumer inventory (historical):** AN_SilkyNvg, AN_Monsters, AN_FluidUI (GameCompositor + main submit loop). The current chain also includes ArcaneSiege/TOMF.

**Verified current distinction:** VALIDATE_USAGE is enabled unconditionally and already guards Map access modes. It does NOT implement the proposed GraphicsDevice.UpdateBuffer GPU-private guard: the current root method checks bounds, then calls the backend. Do not treat the define as evidence that this entire fix landed.

**Separate narrow work (2026-10-31):** GraphicsDevice.UpdateBuffer is now renamed to UpdateBufferOutsideCommandList in the unpublished Veldrid working tree; CommandList.UpdateBuffer unchanged. Release solution build and 47 ContractTests pass. Default-off storage permission AllowUpdateBufferOutsideCommandList is approved but NOT implemented; next session adds it. This acknowledgement does not itself prove synchronization safety or implement the broader memory-class checks below. See BUGFIX_TypedBufferAccessPolicies_IMPL.md for handoff.

---

## 1. Symptom

`GameCompositor_Test` on macOS/Metal:

```
Veldrid.VeldridException: Attempting to write to a MTLBuffer that is inaccessible from a CPU.
   at Veldrid.MTL.MtlGraphicsDevice.UpdateBufferCore(...)
   at Veldrid.GraphicsDevice.UpdateBuffer[T](DeviceBuffer, UInt32, T[])
   at ...SimpleTriangleRenderer.CreateResources()   // vertex/index buffer initial fill
```

The same code ran for months on Windows (D3D11) and Linux (Vulkan) without complaint. The call was

```csharp
vertexBuffer = factory.CreateBuffer(new BufferDescription(size, BufferUsage.VertexBuffer)); // no Dynamic
graphicsDevice.UpdateBuffer(vertexBuffer, 0, vertices);                                     // CPU write
```

Temporary fix shipped in AN_FluidUI (`SimpleTriangleRenderer.cs`): `BufferUsage.VertexBuffer | BufferUsage.Dynamic`.
That makes it work; it is NOT the right declaration for static geometry (see §4).

---

## 2. Root cause — three backends, three truths

`GraphicsDevice.UpdateBuffer` (and `Map`) on a buffer declared WITHOUT `Dynamic`/`Staging`:

| Backend | Memory class chosen for non-Dynamic buffer | `GraphicsDevice.UpdateBuffer` result | Where |
|---|---|---|---|
| **Metal** | `MTLResourceOptions.StorageModePrivate` | **throws** — no CPU pointer | `MTL/MTLBuffer.cs:39`, `MTL/MTLGraphicsDevice.cs:632-642` |
| **Vulkan** | `DeviceLocal` (not host-visible) | **works** — Veldrid allocates a staging buffer, records `vkCmdCopyBuffer`, submits a one-off command buffer | `Vk/VkBuffer.cs:81-87`, `Vk/VkGraphicsDevice.cs:1347-1385` |
| **D3D11** | `ResourceUsage.Default` | **works** — `UpdateSubresource`; the *driver* performs a hidden staging copy | `D3D11/D3D11Buffer.cs:73-79`, `D3D11/D3D11GraphicsDevice.cs:578-592` |

Metal is the only backend telling the truth about the memory. The two "works" rows are a synchronous,
out-of-band GPU copy that happens to be tolerated:

- **Ordering hazard.** `GraphicsDevice.UpdateBuffer` has no command-list context. The Vulkan path submits its
  own command buffer with no barrier against frames in flight that may be READING the same buffer. D3D11's
  `UpdateSubresource` is ordered on the immediate context, which only helps if everything else is too.
- **Perf lie.** A caller streaming a vertex buffer every frame through `UpdateBuffer` on a non-Dynamic buffer
  sees "fast" on D3D11 and stutter/allocation churn on Vulkan — and a crash on Metal. Same source line.
- **Secondary divergence (same family, mechanism not outcome):** D3D11 only `Map`s a Dynamic buffer for a FULL
  update (`useMap = isDynamic && updateFullBuffer`, line 588). A PARTIAL Dynamic update goes through a pooled
  staging buffer + `CopySubresourceRegion` INTO the Dynamic buffer (lines 615-630) — a GPU write to a resource
  D3D11 documents as GPU-read/CPU-write. Metal and Vulkan memcpy the sub-range. Bytes land on all three; whether
  D3D11's mechanism is legal under the debug layer is the §5.6 INVESTIGATION. Not blocking.
- **Tertiary divergence:** CPU READ of a Dynamic buffer. D3D11 Dynamic is `CpuAccessFlags.Write` only;
  Metal `Shared` and Vulkan `HostVisible|HostCoherent` are readable. `Map(buffer, MapMode.Read)` on a
  Dynamic buffer works on two backends and fails on one.

### 2.1 Why `CommandList.UpdateBuffer` is NOT affected

`CommandList.UpdateBuffer` uses the per-command-list staging pool and records a copy INTO the command
stream the caller already orders. Works identically on all three backends for Private/Default/DeviceLocal
buffers. This is the correct path for GPU-owned data; it is the one the compositor and MotionVectorPass
already use for per-frame uniforms.

---

## 3. Decision

Make the backends agree by **adopting Metal's semantics everywhere** (strict), NOT by adding hidden staging
copies to Metal (permissive). Rationale:

- The permissive route spreads the Vulkan ordering hazard to a third backend and hides a GPU copy behind a
  memcpy-looking call. It is the "hack you then write 1000 lines on top of".
- The strict route makes the write path DECLARATIVE: the buffer's declared memory class IS the name of the
  allowed write path. Learn once, correct on every platform.
- Only three consumers exist and all are ours. The breaking change is cheap and the compiler/guard finds
  every site.

Delivered in two phases. **Phase 1 is a prerequisite step toward Phase 2, not an alternative to it.**

---

## 4. The contract (both phases enforce this; Phase 1 at runtime, Phase 2 at compile time)

Three memory classes, one role-set. Memory class determines CPU access + bindability; roles are orthogonal.

| Memory class | CPU write | CPU read | Bindable (vertex/index/uniform/structured/indirect) | GPU fill | Metal | D3D11 | Vulkan |
|---|---|---|---|---|---|---|---|
| **GpuPrivate** (today: no Dynamic/Staging flag) | ✗ | ✗ | ✓ | `CommandList.UpdateBuffer`, `CommandList.CopyBuffer` | Private | Default | DeviceLocal |
| **CpuWritable** (today: `Dynamic`) | ✓ `UpdateBuffer`, `Map(Write)` | ✗ | ✓ | also allowed | Shared | Dynamic + CPU_WRITE | HostVisible\|HostCoherent, persistently mapped |
| **Staging** (today: `Staging`) | ✓ | ✓ `Map(Read)` | ✗ | copy dest only | Shared | Staging + CPU_READ\|WRITE | HostVisible\|HostCoherent(\|Cached) |

Rules:

1. `GraphicsDevice.UpdateBuffer(buffer, ...)` and `GraphicsDevice.Map(buffer, MapMode.Write)` are legal ONLY on
   CpuWritable and Staging.
2. `GraphicsDevice.Map(buffer, MapMode.Read)` is legal ONLY on Staging. (CpuWritable is write-only on D3D11 →
   write-only everywhere.)
3. Partial `UpdateBuffer` / `Map(Write)` of a CpuWritable buffer IS legal on all backends and must land the bytes
   with no API validation error. Metal/Vulkan: memcpy into shared memory. D3D11: currently a staging copy for the
   partial case — whether that satisfies this rule is the §5.6 INVESTIGATION.
4. Staging buffers cannot be bound (`ResourceSet`, `SetVertexBuffer`, `SetIndexBuffer`, indirect args). Phase 1
   guards this at bind time; Phase 2 makes it unrepresentable.
5. GpuPrivate is the DEFAULT and the correct declaration for static data (meshes, index buffers, LUTs). Fill
   once at init via `CommandList.UpdateBuffer`. Do NOT reach for CpuWritable to silence the guard.

Guidance table for consumers (goes in the exception message, §5.2):

| Data pattern | Declare | Fill with |
|---|---|---|
| Static geometry, lookup tables | GpuPrivate | `CommandList.UpdateBuffer` once at creation |
| Per-frame uniforms, streamed vertices (ring buffer) | CpuWritable | `GraphicsDevice.UpdateBuffer` / `Map(Write)` |
| Readback (screenshots, GPU → CPU results) | Staging | `CommandList.CopyBuffer` into it, then `Map(Read)` |

---

## 5. Phase 1 — runtime guard in the base class (DO NOW)

Scope: `src/Veldrid/GraphicsDevice.cs`, `src/Veldrid/DeviceBuffer.cs`, `src/Veldrid/CommandList.cs`,
`src/Veldrid/ResourceFactory.cs`, new `src/Veldrid/GpuBufferSafety.cs`. No type changes. No consumer API changes.
No per-backend changes. Consumers break only where they were already wrong on Metal.

Size (measured against the fork, 2026-10-15): steps 1–4 ≈ 55 mechanical lines. Cost lives in the consumer
audit (§5.5), not in Veldrid.

### 5.1 Already in place — do not re-implement

- [x] `GraphicsDevice.Map` guard — **ALREADY EXISTS upstream** (`GraphicsDevice.cs:465+`, under `#if VALIDATE_USAGE`,
      which `Veldrid.csproj:16` defines unconditionally — so it is always on in this fork): Write requires
      `Dynamic|Staging`, Read requires `Staging`, subresource must be 0. Rules 1 and 2 of §4 already hold for `Map`.
      Step 3 only routes its message text through `GpuBufferSafety` so the remedy guidance is shared.
- [x] Guard posture: upstream's `VALIDATE_USAGE` blocks are effectively always-on here. New guards slot into the same
      `#if VALIDATE_USAGE` blocks — same posture, NOT `GraphicsDeviceOptions.Debug`-gated. The consistency is the feature.

### 5.2 Implementation — steps 1–4

**Step 1 — `GraphicsDevice.UpdateBuffer` root guard** (`GraphicsDevice.cs:826-841`, ~5 lines)

All 7 public `UpdateBuffer` overloads (`:711-826`) funnel into the non-generic root
`UpdateBuffer(DeviceBuffer, uint, IntPtr, uint)`. ONE check there, after the existing size check and BEFORE
`UpdateBufferCore`:

```csharp
#if VALIDATE_USAGE
if (!buffer.IsCpuWritable)
    throw new VeldridException(GpuBufferSafety.CpuWriteToGpuPrivateMessage(buffer));
#endif
```

Per-backend `UpdateBufferCore` implementations are NOT touched. Metal's own throw (`MTLGraphicsDevice.cs:639`)
stays as defence in depth; after step 1 it should be unreachable.

**Step 2 — `DeviceBuffer` predicates** (`DeviceBuffer.cs`, ~10 lines)

Names per linguistic keying — call sites read the memory class, not a flag mask:

- `DeviceBuffer.IsCpuWritable` → `(Usage & (BufferUsage.Dynamic | BufferUsage.Staging)) != 0`
- `DeviceBuffer.IsStaging`     → `(Usage & BufferUsage.Staging) != 0`
- `DeviceBuffer.IsGpuPrivate`  → `!IsCpuWritable`

**Step 3 — `GpuBufferSafety` message builders** (new `GpuBufferSafety.cs`, ~25 lines)

`internal static class GpuBufferSafety` with `CpuWriteToGpuPrivateMessage(DeviceBuffer)`,
`CpuReadFromNonStagingMessage(DeviceBuffer)`, `StagingBufferBoundMessage(DeviceBuffer, string bindPoint)`.
Re-point the existing `Map` guard strings (§5.1) at the first two so `UpdateBuffer` and `Map` give identical
guidance. The text is the UX of this fix — it must name the REMEDY, not just the fault:

```
[Veldrid.GpuBufferSafety] CPU write to GPU-private buffer '{Name}' (Usage={Usage}, {SizeInBytes} B).
GraphicsDevice.UpdateBuffer/Map(Write) require BufferUsage.Dynamic (CPU-streamed data) or BufferUsage.Staging.
  - Static data (meshes, LUTs):   keep the buffer GPU-private and fill it with CommandList.UpdateBuffer.
  - Per-frame / streamed data:    declare BufferUsage.Dynamic.
This is enforced identically on D3D11, Vulkan and Metal (Metal has no CPU path to private storage).
```

Without the two bullets, consumers will slap `Dynamic` on static geometry — which is what the stop-gap in
AN_FluidUI did and which this doc exists to walk back.

**Step 4 — Staging bind-time guards** (`CommandList.cs`, `ResourceFactory.cs`, ~15 lines)

Rule 4 of §4. Reject `IsStaging` in the base class at:

- `CommandList.SetVertexBuffer(uint, DeviceBuffer, uint)` (`:121`, the overload the 2-arg one forwards to)
- `CommandList.SetIndexBuffer(DeviceBuffer, IndexFormat, uint)` (`:153`)
- `CommandList.DispatchIndirect(DeviceBuffer, uint)` (`:644`) and the `DrawIndirect` / `DrawIndexedIndirect`
  overloads — **read their signatures first**; the grep only matched `DispatchIndirect` with the `(DeviceBuffer` shape.
- `ResourceFactory.CreateResourceSet` buffer elements — **unwrap both** `DeviceBuffer` and `DeviceBufferRange`
  (`IBindableResource` may be either), or the guard has a hole.

`CommandList.cs` already has 22 `#if VALIDATE_USAGE` blocks; slot into them.

### 5.3 Checklist

- [ ] Step 1 — root `UpdateBuffer` guard
- [ ] Step 2 — `DeviceBuffer` predicates
- [ ] Step 3 — `GpuBufferSafety.cs` + re-point `Map` guard text
- [ ] Step 4 — Staging bind guards (after reading `Draw*Indirect` signatures + `DeviceBufferRange` unwrap)
- [ ] Build fork; run its tests on Metal (this Mac) and D3D11/Vulkan
- [ ] §5.6 investigation (independent of steps 1–4; can run in parallel or after)

### 5.4 What steps 1–4 change about D3D11 behaviour — and what they do not

After step 1, `D3D11GraphicsDevice.UpdateBufferCore` (`:578-631`) is only ever reached with `Dynamic` or `Staging`
buffers. Of its three branches:

| Branch | Condition | Reachable after step 1? |
|---|---|---|
| `UpdateSubresource` | `!isDynamic && !isStaging && …` | **No — dead code.** Non-Dynamic never gets here. |
| `Map(Write)` + memcpy | `(isDynamic && updateFullBuffer) \|\| isStaging` | Yes — full Dynamic update, any Staging update |
| staging buffer + `CopySubresourceRegion` | everything else = **partial Dynamic update** | Yes |

So steps 1–4 do not need to change D3D11 at all. The dead `UpdateSubresource` branch can be deleted as a tidy-up
or left (harmless). Whether the third branch is correct is the open question in §5.6.

### 5.5 Consumer audit + rollout

Audit is mechanical: grep each consumer for `GraphicsDevice.UpdateBuffer(`, `.Map(`, and the buffer's
`CreateBuffer` site; classify by the §4 guidance table.

| Consumer | Audit | Expected fallout |
|---|---|---|
| **AN_FluidUI** — `FluidUI.GameCompositor_Test/SimpleTriangleRenderer.cs` | - [ ] | Revert stop-gap `Dynamic` → GpuPrivate + `CommandList.UpdateBuffer` at init (option A from the original fix discussion) |
| **AN_FluidUI** — compositor blit quads, main submit loop (`FluidUI.GameCompositor/*`, `FluidUI/*Render*`) | - [ ] | Expect clean: already `CommandList.UpdateBuffer` for uniforms. Verify any static quad VB |
| **AN_SilkyNvg** — `Rendering.Veldrid` vertex/uniform upload | - [ ] | Streaming per-frame → should already be Dynamic. Verify `Map` modes |
| **AN_Monsters** — game renderer | - [ ] | Highest risk: static mesh uploads via `GraphicsDevice.UpdateBuffer` are likely |

Rollout order:

- [ ] 1. Implement §5.1–5.4 in the fork. Build + run the fork's own tests on Metal (this Mac) and D3D11/Vulkan.
- [ ] 2. `cmd/publish-local.cs`. Note the version.
- [ ] 3. For each consumer: bump pin → build → run on Metal first (strictest real-world check) → fix sites per §4 → run on D3D11/Vulkan.
- [ ] 4. Only after all three consumers are clean on all backends: remove the per-backend Metal throw? **No — keep it.** It is defence in depth and costs nothing.

### 5.6 INVESTIGATION — does the D3D11 *Dynamic* update path need to change at all?

**Status: open. Not a prerequisite for steps 1–4.** Steps 1–4 make the non-Dynamic (`UpdateSubresource`) path
unreachable, so the ONLY D3D11 question left is the Dynamic one.

**Correction (2026-10-15):** an earlier draft of this doc claimed a partial Dynamic update "falls to
`UpdateSubresource`, which D3D11 forbids on DYNAMIC". That was a misread of one line. The actual code
(`D3D11GraphicsDevice.cs:583-630`):

| Dynamic update | Path taken | D3D11 legality |
|---|---|---|
| full buffer (`offset == 0 && size == SizeInBytes`) | `Map(MapMode.Write)` → `VdToD3D11MapMode(isDynamic: true, Write)` (= `WRITE_DISCARD`) + memcpy, `Unmap` | ✓ textbook |
| partial (any sub-range) | pooled **Staging** buffer ← CPU write, then `CopySubresourceRegion(dst: the Dynamic buffer)` | **?** — `D3D11_USAGE_DYNAMIC` is documented as GPU-read / CPU-write; a copy INTO it is a GPU write |

Metal and Vulkan do a plain memcpy into shared/host-visible memory for both rows. So the three backends already
agree on *observable* semantics (bytes land, no API error observed); they differ on *mechanism*, and D3D11's
partial-row mechanism is the one with a legality question mark.

Mitigating evidence: this is unmodified upstream Veldrid (ppy fork) code that has shipped in production for years.
If the debug layer rejected it, it would be a known upstream issue. Confidence that it is a practical bug: low.

**Questions to answer (in order; stop when one gives a definitive answer):**

1. [ ] With `GraphicsDeviceOptions.Debug = true` (D3D11 SDK layers on), run a partial `UpdateBuffer` on a Dynamic
       vertex buffer AND a Dynamic uniform buffer. Does `D3D11_MESSAGE_ID_COPYSUBRESOURCEREGION_INVALIDDESTINATIONSTATE`
       (or any copy-related validation error) fire? **A clean debug-layer run closes this investigation as "no change".**
2. [ ] Microsoft's `ID3D11DeviceContext::CopySubresourceRegion` remarks — confirm/refute "destination may not be
       `D3D11_USAGE_DYNAMIC`" from the documentation, not memory. Record the exact sentence + URL here.
3. [ ] Does any consumer actually perform PARTIAL Dynamic updates on D3D11? Grep `GraphicsDevice.UpdateBuffer(` with a
       non-zero offset or a size < buffer size across AN_SilkyNvg / AN_Monsters / AN_FluidUI. If none do, the row is
       theoretical and this investigation is deprioritised regardless of 1–2.

**If the answer is "change":** replace the partial-Dynamic branch with `Map(MapMode.WriteNoOverwrite)` + offset
memcpy (no staging buffer, no GPU copy — matches Metal/Vulkan mechanism). Then and only then the FL 11.1 question
applies: `WRITE_NO_OVERWRITE` on a **constant** buffer requires feature level 11.1 (the fork requests 11.1 first,
`D3D11GraphicsDevice.cs:104`, falling back to 11.0 / 10.0). Options: (a) require FL 11.1 (modern Windows only — likely
acceptable), (b) on < 11.1 fall back to full-buffer `WRITE_DISCARD` + CPU shadow copy for uniform buffers only.

**If the answer is "no change":** record the evidence here, mark §4 rule 3 as satisfied by the existing code, and
optionally delete the now-dead `UpdateSubresource` branch as a tidy-up.

Either way: `MapMode.WriteNoOverwrite` is NOT currently exposed to the partial `UpdateBuffer` path — `UpdateBufferCore`
calls `MapCore(buffer, MapMode.Write, 0)` unconditionally. Any change here is confined to
`D3D11GraphicsDevice.UpdateBufferCore`.

---

## 6. Phase 2 — type-space enforcement (DEFERRED; design recorded so Phase 1 does not paint us into a corner)

Move the memory class out of `BufferUsage` and into the C# type. The compiler then rejects every wrong call
that Phase 1 rejects at runtime, plus the bindability rules. This is the correct end state; it is deferred
because it is a wide mechanical refactor and not needed to make the three backends agree.

### 6.1 Type hierarchy

```
DeviceBuffer                                  (abstract; SizeInBytes, Roles, Name) — what CommandList.CopyBuffer / UpdateBuffer accept
├── DeviceBuffer_Bindable                     (abstract) — what ResourceSet / SetVertexBuffer / SetIndexBuffer / *Indirect accept
│   ├── DeviceBuffer_GpuPrivate               no CPU surface at all
│   └── DeviceBuffer_CpuWritable              + UpdateBuffer(...), + Map(MapMode.Write) — implements IDeviceBuffer_CpuWritable
└── DeviceBuffer_Staging                      + UpdateBuffer(...), + Map(Read|Write|ReadWrite) — implements IDeviceBuffer_CpuWritable, IDeviceBuffer_CpuReadable
```

- `BufferUsage` loses `Dynamic` and `Staging`; renamed to `BufferRoles` (`VertexBuffer | IndexBuffer | UniformBuffer |
  StructuredBufferReadOnly | StructuredBufferReadWrite | IndirectBuffer`). Roles remain combinable flags.
- `UpdateBuffer` and `Map` move OFF `GraphicsDevice` onto the CPU-visible types. Signature shape unchanged
  (`buffer.UpdateBuffer(offset, span)`), so most call sites are a receiver swap.
- `ResourceFactory`:
  - `CreateGpuPrivateBuffer(uint sizeInBytes, BufferRoles roles)`
  - `CreateCpuWritableBuffer(uint sizeInBytes, BufferRoles roles)`
  - `CreateStagingBuffer(uint sizeInBytes)` — no roles; it cannot be bound
  - `CreateBuffer(BufferDescription)` kept one release as `[Obsolete]`, returning the matching subclass at runtime
    (it already returns the abstract base, so this is source-compatible for callers that only hold `DeviceBuffer`).
- `BufferDescription` becomes internal / removed with the obsolete factory.

### 6.2 Backend impact

Each backend's buffer class (`MtlBuffer`, `VkBuffer`, `D3D11Buffer`) currently branches on the usage flags at
construction (the lines cited in §2). Under Phase 2 each becomes three thin subclasses (or one class with a
`MemoryClass` enum fed by the factory) — the memory-selection logic does not change, only where the decision is
made (type, not flag).

### 6.3 What Phase 1 must NOT do, so Phase 2 stays cheap

- Do not add new public `BufferUsage` flags.
- Put the three predicates (`IsCpuWritable`/`IsStaging`/`IsGpuPrivate`) on `DeviceBuffer`; Phase 2 replaces them
  with the type test and the names stay meaningful in the interim.
- Keep all guard text in `GpuBufferSafety`; Phase 2 deletes the class rather than hunting messages.

### 6.4 Rollout (when scheduled)

- [ ] Fork: add types + typed factory methods; `[Obsolete]` the old factory and `GraphicsDevice.UpdateBuffer/Map(buffer)`
      with messages naming the typed replacement. Publish.
- [ ] Consumers: bump; fix every `CS0618` (that IS the audit); publish each.
- [ ] Fork: delete the obsolete surface + `GpuBufferSafety` runtime guards (now unrepresentable). Major bump. Publish.

---

## 7. Open questions

- **D3D11 Dynamic partial update** — see §5.6 (INVESTIGATION). Only if that investigation concludes "change to
  `Map(WriteNoOverwrite)`" does the FL 11.1 constant-buffer question arise (`WRITE_NO_OVERWRITE` on constant buffers
  needs 11.1; the fork requests 11.1 first — `D3D11GraphicsDevice.cs:104`, falls back to 11.0 / 10.0).
- **Vulkan `GraphicsDevice.UpdateBuffer` on CpuWritable (persistently mapped) is a raw memcpy with no
  synchronization against in-flight reads.** Same as D3D11 `WRITE_NO_OVERWRITE` contract — caller owns the ring
  discipline. Document this explicitly in the `IDeviceBuffer_CpuWritable` XML doc in Phase 2; is there appetite for
  a debug-only "buffer written while referenced by an unsignalled fence" check? (Out of scope here.)
- **`UpdateTexture` has the identical three-way split** (`UpdateTextureCore` on all three backends). Textures are
  not covered by this doc; file a sibling once Phase 1 lands and the pattern is proven.

---

## 8. Related

- Metal swapchain point/pixel fix shipped alongside the stop-gap: `MTLSwapchain.cs` honors
  `SwapchainDescription.Width/Height` (fork commit `6d2ee67`, package `5.261014.225946`).
- AN_FluidUI stop-gap: `SimpleTriangleRenderer.cs` `BufferUsage.Dynamic` on static VB/IB (commit `f369567e`).
  Reverted by Phase 1 §5.5 row 1.
- `../../AN_FluidUI/_TASKS/metal-stencil-broken.md`, `../../AN_FluidUI/_TASKS/metal-precompiled-shader-binding.md` — other Metal-only divergences
  found by running the same code on all three backends.