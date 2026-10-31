# Typed buffer access and update policies

Status: first explicit-allocation slice implemented and ContractTests passing; broader mechanism audit/support and Silky migration still unfinished.
Parent: [Shader interface handoff](BUGFIX_ShaderInterfaceContract_IMPL_Tracking.md).

## Agreed decisions (2026-10-31)
- Rename `[DynamicUniform]` to `[UniformBufferBindingTimeOffsetSelectable]`. This declares binding-offset selection, NOT allocation or update behavior.
- Typed safety must support actual GPU access mechanisms with explicit portable restrictions, including declaration requirements needed by stricter backends (Metal included). Do not rely on permissive drivers.
- Shader name/type/layout verification and storage/access/update policies are independent contracts.
- Do not force `BufferUsage.Dynamic` on all shape-owned buffers, or blindly replace every allocation with another universal policy.
- Preserve Silky's existing non-Dynamic view-uniform buffers and command-ordered full-block updates (D3D11 UpdateSubresource). Do not reopen the established Silky workaround.
- Keep GPU buffers private and writes typed/named; no raw binding escape hatch.
- Preserve paint dedupe, default element, segmented rollover, and one full upload per segment residency change.
- Stop and discuss unexpected trouble, substantive policy choices, or API mismatches. Do not restructure Silky or weaken tests to fit an incomplete API.
- Every Silky tests/graphical test must pass on D3D11 and Vulkan, including previously failing tests. Report skips and blockers; do not regenerate golds merely to get green.
- Pause before Silky publication or downstream migration.
- User clarified: explicit choices belong at native lifecycle points, not uniformly on shader declarations. Creation declares capabilities; operations choose legal updates/maps/copies.
- No mandatory CPU shadow/staging API and no hidden full-buffer upload for a field edit. Typed verification must not impose extra data movement.

## Original code mismatch (allocation issue fixed in working tree below)
- `src/Veldrid/ShaderBundle/ShaderPipelineShape.cs` previously allocated owned uniform buffers with UniformBuffer | Dynamic during materialization.
- `UniformBlock<T>.Update` forwards a whole typed value to CommandList.UpdateBuffer; UpdateMember forwards a partial write at a reflected offset.
- `src/Veldrid/D3D11/D3D11CommandList.cs`: non-Dynamic, non-staging full uniform updates take UpdateSubresource; full Dynamic writes take mapped WriteDiscard.
- Silky `VeldridRenderer_Pipelines.cs` deliberately allocates ViewSize without Dynamic; comments record torn state with mapped Dynamic updates during layer transitions.
- `ShaderUniformArena.cs` uses non-Dynamic arena storage. Binding-offset selection and storage policy are already demonstrably distinct.
- These are source findings, not a fresh reproduction of the historical torn-state bug.

## Fresh baseline
| Gate | Result |
|---|---|
| Silky net9.0 renderer build | exit 1; 91 errors, 26 warnings |
| ShaderShapes Release | exit 0; 34 passed, 0 failed/skipped |
| PaintArena Release | exit 0; 14 passed, 0 failed/skipped |
| Full graphical suite | not run; renderer still broken |
Logs: AN_SilkyNvg/TestResults/typed-shape-migration/{build,shapes,arena}-baseline.log.

## Phases
### A. Mechanism matrix and evidence
- [ ] Audit backend implementations and native documentation; save evidence under _EXTERNAL_APIS.
- [ ] Inventory creation declarations, GPU roles, upload/copy, map modes, readback, range binding, alignment, ordering, ownership and lifetime.
- [ ] Separate native support, current Veldrid support, proposed typed support and actual tested support.
- [ ] Explicitly identify unsupported combinations rather than silently selecting alternate semantics.

### B. Typed policy design
- [x] Decide lifecycle placement: creation choices at resource creation; bindings at binding; operation options at update/map/copy. Explicit usage implemented for existing typed uniform/vertex/arena storage only.
- [ ] Define command-recorded versus immediate write contracts and GPU in-flight restrictions.
- [x] User rejected mandatory staging/shadow copies: direct supported member writes, reject unsupported operation/range, no silent widened uploads.
- [x] No implicit allocation defaults for typed uniform/vertex/arena creation; preserve explicit optional arena staging.
- [x] Reviewed the above choices with user before implementation. Further substantive choices still require discussion.

### C. Implementation and Veldrid validation
- [ ] Rename attribute and related misleading application-level symbols across declarations, tests and consumer code; keep native API terminology where accurate.
- [ ] Implement approved policies, starting with preserving Silky's non-Dynamic sequenced view updates.
- [ ] Tests: update/draw/update/draw readback, arena selection, illegal access rejection, backend allocation/update routes.
- [ ] Audit all existing typed buffer types; do not claim all mechanisms supported from uniform-only implementation.
- [ ] Report tested/untested platforms explicitly; Windows tests do not establish Metal correctness.

### D. Silky and acceptance
- [ ] Complete renderer migration using named slots; preserve batching, eviction, lifetime and pipeline-before-bind order.
- [ ] All unit projects; every tests/graphical project on D3D11 and Vulkan.
- [ ] Tiny arena rollover plus memory/performance gates.
- [ ] Exact test inventory/counts, logs/exit codes and any skips/blockers.
- [ ] User validation report before publishing Silky or proceeding downstream.

## Open choices
- Broader typed resource sharing, immediate writes, map/copy/readback operations, immutable initialization and explicit native capabilities remain to implement; existing BufferUsage choices do not claim complete access-policy coverage.
- Native support/version constraints, especially Metal resource declarations and synchronization.
- Availability of a Metal execution environment; no assumption of local coverage.

## First implementation slice (2026-10-31)
- Shape binding creates verified handles/layouts but NO uniform storage. UniformBlock.CreateBuffer(BufferUsage) requires an explicit allocation choice and rejects incompatible roles in Release.
- VertexInput.CreateBuffer(capacity, usage) and UniformBlock.CreateArena(device, capacity, usage) require explicit allocation choices too.
- Renamed offset attribute and internal offset-selection symbols in Veldrid. Silky source/package migration still pending.
- Whole typed updates retain the existing backend call; no new CPU shadow, mandatory staging, or extra upload. D3D11 partial UpdateMember rejects unsupported current route without widening; other backend paths remain direct.
- ImGui creates non-Dynamic projection storage explicitly; its vertex allocation explicitly preserves Dynamic.
- New tests cover missing/invalid/duplicate allocation, disposed updates, preserved allocation flags, D3D11 partial rejection and update/draw/update/draw/update/draw readback on D3D11/Vulkan. Vulkan also exercises direct named-member updates without a whole-block shadow/upload.
- Fresh Release ContractTests: 47 passed, 0 failed, 0 skipped; exit 0. Command: dotnet test src/Veldrid.ContractTests/Veldrid.ContractTests.csproj -c Release. Log: C:/TEMP/veldrid-explicit-buffer-tests.log.
- Release solution build: 0 errors, 1186 warnings, exit 0. Log: C:/TEMP/veldrid-explicit-buffer-build.log. git diff --check passed (line-ending notices only).
- Expected StableABI changes accepted via /p:UpdateStableABI=true, then tests rerun WITHOUT the update flag. No publication or downstream validation yet.

## Session handoff: device update naming checkpoint
- DONE: all seven public GraphicsDevice.UpdateBuffer overloads renamed to UpdateBufferOutsideCommandList. No alias retains the ambiguous name. Four backend staging-fill call sites and the telemetry XML link migrated; CommandList.UpdateBuffer and private protected backend UpdateBufferCore hooks unchanged.

## C.1 AllowUpdateBufferOutsideCommandList permission (decided 2026-10-31, implementing now)

### The problem this flag names
`GraphicsDevice.UpdateBufferOutsideCommandList` is an immediate host write with NO position in any command list. Every not-yet-submitted
command list - including commands recorded BEFORE the call - observes the new bytes; a still-executing list races it. `CommandList.UpdateBuffer`
is a recorded copy: commands before it see old bytes, commands after see new (verified in all three `UpdateBufferCore`s:
`VkCommandList.cs:234`, `MTLCommandList.cs:1333`, `D3D11CommandList.cs:1214`). The defect class is a programmer ASSUMING sequencing from the
device-level call (the historical Silky "last paint wins" bug). The flag is the caller's explicit acknowledgement that the write is unordered
and that they own synchronisation. It is NOT a memory-class flag and does not alter native allocation.

**THE RULE - the only legal use of `UpdateBufferOutsideCommandList` (user, 2026-10-31):** BOTH must hold:
  (a) nothing is using that buffer in any pending (recorded-but-not-complete) command list, AND
  (b) nothing will use that buffer until the caller has done a WaitForIdle (or equivalent fence wait) after the write.
"Once per frame", "only at init", "it's a uniform", "it's Dynamic" are NOT justifications. The previous frame's submission may still be reading
(no render loop here waits for idle between frames). Veldrid's internal staging pools and Monsters' `StaticDeviceBufferUpload`
(fresh Staging -> CopyBuffer -> WaitForIdle -> dispose) satisfy (a)+(b). Every other consumer site found below does not.

**Consumer purge requirement:** when each consumer is migrated, every `GraphicsDevice.UpdateBuffer*` site that fails (a)+(b) is converted to
`CommandList.UpdateBuffer`, and every comment rationalising the device-level call ("fine because per frame", "immediate is OK here", etc.) is
DELETED, not reworded. The grep for the method name after migration must return only sites that satisfy the rule and declare the flag.

**Rejected alternative - exclusive flag (`OnlyUpdateBufferOutsideCommandList`, considered 2026-10-31):** making the flag a classification that
forbids `CommandList.UpdateBuffer` / CopyBuffer-destination on the same buffer. Rejected by user counter-example: device-level INITIAL LOAD ->
WaitForIdle -> later `CommandList.UpdateBuffer` of the same buffer in a FUTURE command list is valid and realistic (static mesh with patches,
LUT regenerated on settings change). The two mechanisms are legitimately separated in TIME by a fence, not by buffer. The invariant is temporal
and cannot be enforced by a creation-time flag for either mechanism; the flag is therefore a non-exclusive PERMISSION whose job is to force the
caller to state they understand (a)+(b). Do not re-propose exclusivity.

### Consumer inventory of `GraphicsDevice.UpdateBuffer` (old name; none migrated yet) and visibility decision
| Repo | Sites | Pattern | Correct fix at migration |
|---|---|---|---|
| AN_SilkyNvg | 0 code (2 doc mentions) | all already `commandList.UpdateBuffer` | none |
| AN_FluidUI | 2 | `SimpleTriangleRenderer` stop-gap Dynamic static VB/IB fill | `CommandList.UpdateBuffer`, revert Dynamic (450 §5.5) |
| AN_Monsters | 13 | init fills + per-frame uniform writes; `StaticDeviceBufferUpload` staging fill | init/per-frame -> CL; staging helper -> flag `true` |
| AN_ArcaneSiege | 21 | init fills; per-frame uniforms (TileBatch, ChunkInstanced); `DebugQuadRenderer` update/draw/update/draw | all -> `CommandList.UpdateBuffer` |

Decision (user, 2026-10-31): **Option A now** - method stays public, flag required. The exception at each consumer site is the migration audit.
**Option C recorded as uncertain follow-up:** after downstream migration H-K, re-grep; if the only surviving legitimate use is "fill a fresh
Staging buffer before CopyBuffer", replace it with a narrow typed staging-upload API and make `UpdateBufferOutsideCommandList` internal.
Making it internal NOW was rejected because `GraphicsDevice.Map(…, Write)` is the same unordered host write with no permission check, so
consumers would just move the hazard to a less obvious method; going internal must gate Map(Write) identically or gain nothing.
- [ ] (C, deferred) Re-evaluate `UpdateBufferOutsideCommandList` visibility after H-K; consider extending the permission to `Map(Write)`.

### Decisions
- Location: new `public bool AllowUpdateBufferOutsideCommandList` field on `BufferDescription` (`src/Veldrid/BufferDescription.cs`),
  participating in `Equals`/`GetHashCode`; exposed read-only as `DeviceBuffer.AllowUpdateBufferOutsideCommandList { get; internal set; }` on the abstract base (`src/Veldrid/Buffer.cs`),
  stamped by `ResourceFactory.CreateBuffer(ref BufferDescription)` after `CreateBufferCore`. Backends are NOT touched: this is a Veldrid-level
  validation contract, not a native property (user correction 2026-10-31; an initial per-backend override plumbing was reverted).
- Default OFF. Existing three `BufferDescription` constructors keep their meaning (flag false); one new constructor overload adds the flag
  as a trailing `bool allowUpdateBufferOutsideCommandList` parameter. No new `BufferUsage` bit (spec 450 §6.3).
- Check is UNCONDITIONAL (not `#if VALIDATE_USAGE`, not Debug-gated) in the single root
  `GraphicsDevice.UpdateBufferOutsideCommandList(DeviceBuffer, uint, IntPtr, uint)` (`GraphicsDevice.cs:939`), after the size check,
  before `UpdateBufferCore`. All seven public overloads funnel there. `CommandList.UpdateBuffer` is untouched.
- Exception type `VeldridException`. Text (names the remedy, per user):
  `BufferDescription.AllowUpdateBufferOutsideCommandList must be set on buffer '{Name}' (Usage={Usage}, {SizeInBytes} B) in order to use GraphicsDevice.UpdateBufferOutsideCommandList(). This write is NOT ordered relative to recorded commands; use CommandList.UpdateBuffer for writes that must be sequenced within a command list.`
- NO implicit grant for `BufferUsage.Staging` or `Dynamic`. Memory class and write-ordering acknowledgement are orthogonal: Staging says
  "CPU-visible copy endpoint", not "I accept unordered writes". A user Staging readback buffer (CopyBuffer -> Map(Read)) never needs the flag.
- Internal backend staging POOL buffers opt in explicitly at their five creation sites, because the backend owns their fill -> record-copy -> recycle
  discipline: `D3D11CommandList.cs:1047`, `D3D11GraphicsDevice.cs:752`, `VkCommandList.cs:1041`, `VkGraphicsDevice.cs:1240`, `MTLCommandList.cs:728`.
  These are the ONLY callers of the device-level method inside Veldrid (grep verified). Do not bulk-enable anything else.
- REVISED after reading the code: typed ShaderBundle storage (`UniformBlock.Update/UpdateMember`, `ShaderVertexBuffer.Update`,
  `ShaderUniformArena.Upload`) writes ONLY through `CommandList.UpdateBuffer`, and the underlying `DeviceBuffer` is `internal`. No public path
  can reach the device-level method on typed storage, so an `allowUpdateBufferOutsideCommandList` creation parameter would enable nothing.
  Typed storage is therefore sequenced-by-design and is created with the flag false (no API change). Any future unordered typed write path
  (e.g. a device-updated Dynamic vertex ring for perf) is a separate explicit API discussion, not a pre-baked bool.
- ImGui (`ImGuiRenderer.cs`): `_vertexBuffer` via `ShaderVertexBuffer.Update`, `_indexBuffer` via `cl.UpdateBuffer` (line 307), projection via
  `Projection.Update(cl, ...)`. All command-list ordered; no ImGui buffer needs the flag. No change.
- Observed, NOT changed: `CommandList.UpdateBuffer` targeting a Staging buffer is a legal (pointless) GPU copy into CPU-visible memory on all
  three backends. Out of scope; spec 450 bind/role guards remain deferred.
- OpenGL backend: compile-only; no runtime validation available on this machine. Metal likewise. Report as untested.

### Implementation checklist
- [x] `BufferDescription`: field, ctor overload, Equals/GetHashCode, XML doc explaining ordering semantics.
- [x] `DeviceBuffer.AllowUpdateBufferOutsideCommandList` concrete base property; stamped in `ResourceFactory.CreateBuffer`. No backend edits.
- [x] Root guard in `GraphicsDevice.UpdateBufferOutsideCommandList(DeviceBuffer, uint, IntPtr, uint)`; XML doc updated.
- [x] Five internal staging-pool creation sites opt in.
- [x] Typed APIs / ImGui: no change required (see REVISED note above).
- [x] Tests (ContractTests): (a) device-level update on a buffer WITHOUT the flag throws `VeldridException` whose message contains
      `AllowUpdateBufferOutsideCommandList`, for Dynamic, Staging and plain GPU-private usages; (b) WITH the flag succeeds and readback matches;
      (c) `CommandList.UpdateBuffer` on a buffer WITHOUT the flag still works (unaffected); (d) `DeviceBuffer.AllowUpdateBufferOutsideCommandList`
      reflects the description; (e) `BufferDescription` equality distinguishes the flag; (f) typed `CreateBuffer(..., false)` then
      device-level update of the owned storage is rejected - via a public path only, no reflection into private buffers.
- [x] `dotnet build Veldrid.sln -c Release` exit 0; accept expected StableABI deltas with `/p:UpdateStableABI=true`, then rebuild/test WITHOUT it.
- [x] `dotnet test src/Veldrid.ContractTests -c Release` all pass; record counts and log paths in the tracking file.
- [ ] Update `BUGFIX_ShaderInterfaceContract_IMPL_Tracking.md` checkpoint; commit Veldrid; `cmd\publish-local.cmd`; record version.

### Then (same session, per user): Silky
- [ ] Bump AN_SilkyNvg Veldrid floor to the new package; migrate `[DynamicUniform]` -> `[UniformBufferBindingTimeOffsetSelectable]`, new explicit
      creation arguments, and every `GraphicsDevice.UpdateBuffer` -> `UpdateBufferOutsideCommandList` with a deliberate flag decision per buffer.
- [ ] Continue the typed-shape migration worklist in `BUGFIX_ShaderInterfaceContract_IMPL_Tracking.md` (A-G) until every
      `AN_SilkyNvg/tests/graphical/**` test passes on D3D11 and Vulkan. Stop and discuss the shared view-buffer API gap before working around it.

## C.2 Shared typed uniform storage: `ShaderUniformBuffer<T>` (approved 2026-10-31, implementing)

**Governing rule (user):** Silky's GPU behaviour must NOT change. The migration fits what Silky already does into the typed binding
abstraction; where the abstraction cannot express existing behaviour, FIX THE ABSTRACTION in Veldrid, never restructure Silky to fit.

**Existing GPU behaviour to preserve byte-for-byte:**
| Buffer | Usage | Size | Updates | Shared by |
|---|---|---|---|---|
| `_viewSizeUniformBuffer` | `UniformBuffer` (non-Dynamic; D3D11 UpdateSubresource sequencing, torn-state note at Pipelines.cs:1395) | 32 B | `commandList.UpdateBuffer`, several per flush | every resource set of every family (~25 sets, 30 shapes) |
| `_textGammaUniformBuffer` | `UniformBuffer \| Dynamic` | 16 B | `commandList.UpdateBuffer` per flush | textured / sweeper / blend / clip-mask sets |

**Gap:** `UniformBlock<T>` is one-slot-owns-one-buffer. Thirty `ViewSize` slots today bind ONE buffer with ONE upload. The arena is the
same shape (one storage -> many blocks of one verified T) but exists for element selection with binding offsets; using a one-element arena
would change the binding mode. Per-family buffers would multiply uploads. Both rejected.

**API (Veldrid, `src/Veldrid/ShaderBundle/ShaderUniformBuffer.cs` + `ShaderPipelineShape.cs`):**
- `public sealed class ShaderUniformBuffer<T> : IDisposable` - `internal DeviceBuffer Buffer`, `internal ResourceFactory Factory`,
  `public void Update(CommandList cl, in T value)` = exactly one `cl.UpdateBuffer(Buffer, 0, value)`. No device-level path. Raw buffer never public.
- `UniformBlock<T>.CreateSharedBuffer(BufferUsage usage)` - same checks and `BufferDescription(Compiled.SizeBytes, usage)` as `CreateBuffer`,
  but returns the storage handle and attaches it to this block.
- `UniformBlock<T>.Share(ShaderUniformBuffer<T> storage)` - attach to another block verified against the same T: same factory, this block's
  `Compiled.SizeBytes == sizeof(T)`, `SetResource(storage.Buffer)`. Safe by construction: every destination block was name/type-verified
  against T at Bind (the same argument already accepted for the arena feeding PaintParams/GradientParams/ImagePatternParams).
- `UniformBlock<T>.Update/UpdateMember` refuse to write when the bound resource is shared storage (write through the storage), mirroring the
  existing arena guard. `Dispose` of a block never disposes shared storage.
- NOT added: `SharedResourceSet<T>` (rejected in handoff; that shares SETS, this shares STORAGE).

**Tests (ContractTests):** two shapes share one `ShaderUniformBuffer`; one `Update` between draws is observed by both (D3D11 + Vulkan readback);
`Share` across factories rejected; `Update` on a sharing slot rejected; `Share` onto a block that already owns storage rejected; disposing the
storage makes both slots fail `RequireLiveResource`; usage flags preserved (non-Dynamic and Dynamic both allowed via `RequireUniform`).

- [ ] Veldrid: ShaderUniformBuffer.cs, CreateSharedBuffer/Share, guards, XML docs.
- [ ] Veldrid: tests above; Release build; StableABI accept; ContractTests all pass; commit; publish-local; record version.
- [ ] Silky: `_viewSizeUniformBuffer` -> `ShaderUniformBuffer<RenderTargetTransformUniform>` (non-Dynamic), `_textGammaUniformBuffer` ->
      `ShaderUniformBuffer<Vector4-equivalent block type>` (Dynamic); every family `Share`s. Existing update call sites become `storage.Update(cl, …)`
      one-for-one. Upload counts unchanged.
- Fresh verification: dotnet build Veldrid.sln -c Release => exit 0, 0 errors, 1186 warnings; ContractTests Release => exit 0, 47 passed, 0 failed/skipped. Logs C:/TEMP/veldrid-outside-command-list-{abi,build,tests}.log. No Metal/OpenGL runtime verification.
- NEXT (approved, NOT implemented): default-off buffer/storage permission AllowUpdateBufferOutsideCommandList checked unconditionally in the device-level root method. Permission is an acknowledgement of caller-owned synchronization, not proof of safe ordering and not a memory allocation flag. Explicitly opt in internal backend staging buffers as needed; do not blanket-enable buffers. Include focused negative/positive tests and ensure command-list writes remain unaffected.
- User narrowed this checkpoint to naming, compilation and handoff due to session size. No BufferDescription/DeviceBuffer permission changes have been made.
- Broad compatibility work is DEFERRED: canonical spec moved from AN_FluidUI/_BUGFIX/450_GPUBuffer_Safety.md to AN_Veldrid/_BUGFIX/450_GpuBuffer_UpdatePath_Compatibility.md. Existing VALIDATE_USAGE covers Map restrictions but not the missing device-level GPU-private update guard from that spec.
- Once narrow work is validated, publish/bump Veldrid as needed for Silky (not yet done). Migrate Silky's offset attribute and new required creation arguments; ordinary ViewSize storage must stay shared with existing upload counts. Its multiple shader families currently share ONE raw view buffer, whereas current UniformBlock owns storage per slot; this mismatch needs a proper typed sharing solution, not repeated uploads or a one-element-arena workaround without discussion.
- Silky baseline remains 91 renderer errors against the published runtime, ShaderShapes 34/34 and PaintArena 14/14. Those linked-source tests do not build the renderer. Full graphical suite not run; EVERY tests/graphical test on D3D11 and Vulkan is required, then report and pause before Silky publication/downstream migration.
- Current changes uncommitted and unpublished: Veldrid source/tests/ABI/docs; FluidUI spec move/link; ArcaneSiege _EXTERNAL_APIS/BufferPolicies snapshots. Silky unchanged. Do not describe the repos as clean or synchronized checkpoints.