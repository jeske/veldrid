# Shader interface contract - implementation tracking and handoff

Updated 2026-10-31 15:05. **READ THIS FIRST in the next session.**
Design/rationale: `BUGFIX_ShaderInterfaceContract_NameVerified.md` in this directory.
This tracking file is the authority for current status; historical checked boxes and green gates in the design do NOT mean S8 is complete.

## Immediate next task

Finish the **AN_SilkyNvg Veldrid renderer migration** to mandatory typed shapes. The last renderer build has **91 errors** from remaining raw layout/resource-set/binding calls. Do not resume TOMF gameplay or publish Silky as working yet.

There is no process/test running that needs to be kept alive. No critical reasoning is intentionally left only in chat.

## Non-negotiable decisions (do not re-open)

- User owns all consumers; breaking/removing public APIs is explicitly authorised. Consumers are sibling repos under `C:\PROJECTS`.
- Alignment must be verified by semantic **field name**, not just location/size/type. Color/position/UV sharing vec4 does not prove meaning.
- Missing verification metadata must FAIL, not silently skip. User's goal is no public path to a shader pipeline with unchecked C# <-> shader <-> shader mappings.
- Precompiled and runtime-compiled GLSL use the SAME compile/verify/bundle/cache/load implementation. Do not introduce separate fixes in each consumer.
- Current public design: sealed partial class deriving ShaderPipelineShape, [ShaderBundle("ExactBundleName")], named UniformBlock<T>/VertexInput<T>/TextureSlot/SamplerSlot fields.
- Stored uniform members and vertex fields must match GLSL names. Slot-numbered CommandList methods and raw pipeline/set/layout/shader factory methods are internal.
- No RawShaders public escape hatch and no ImGui friend-assembly bypass. ImGui is being migrated normally to a GLSL-derived embedded bundle + typed shape.
- Bad values in correctly named slots remain application bugs; do not expand into proving shader maths or choosing the correct material.
- SharedResourceSet<T> abstraction was rejected as unnecessary. Do not invent it while migrating.
- Source generator (S9) is DEFERRED. Keep shapes partial; runtime reflection is acceptable now. Do not leave an arbitrary override able to forge a descriptor.
- Extracting ImGui/StartupUtilities to AN_VeldridUtils is DEFERRED.
- Test bounded capture-and-exit or offscreen/readback scenarios. A game running without crashing is NOT a visual test.

## Repository/package state

Code commits below are before the handoff documentation commits; use git log for the final checkpoint IDs.

| Repo | Code checkpoint | Current consumer dependency floor / state |
|---|---|---|
| AN_Veldrid | 8b402229 S8 checkpoint; 7d3d49b1 typed arenas | Published runtime **5.261031.143705**, including ImGui package. Release tests 41 passed. |
| AN_VeldridSpirv | efb170c | Latest published compiler **5.261031.121914**; source still references Veldrid **5.261031.121302** (old API). Needs migration before rebuilding against S8. |
| AN_SilkyNvg | **5e0d8bab** full in-progress migration checkpoint | Runtime floor **[5.261031.143705,)**; compiler floor **[5.261031.114910,)**. Renderer currently broken; do NOT publish it as validated. |
| AN_FluidUI | e6da1957 | Still old API: Veldrid [5.261031.114508,), SPIRV [5.261031.114910,), Silky [1.261031.115316,). Last published FluidUI **0.261031.115758**. |
| AN_Monsters | 0cf0651 code; **145068a** handoff | Old S7 API: Veldrid [5.261031.122406,), SPIRV [5.261031.121914,), FluidUI 0.261031.115758. Direct RenderKit project reference into ArcaneSiege. |
| AN_ArcaneSiege | fc9d1e0 code; **ab6a68d** handoff | Old S7 API: Veldrid [5.261031.122406,), SPIRV [5.261031.121914,), FluidUI [0.261031.115758,). TOMF gameplay parked. |

NuGet ranges resolve the lowest satisfying version: publishing alone does NOT move consumers. Bump each incoming Build.props floor deliberately.
Local feed: `C:\PROJECTS\LocalNuGet`, via LOCAL_NUGET_REPO. Other machines must republish the dependency chain; user accepts this.
Publish: Veldrid/Silky/FluidUI use `cmd\publish-local.cmd`; SPIRV currently has `cmd\publish-local.ps1 -Release` (no .cmd wrapper). Monsters/RenderKit is a project reference, not a package in this chain.

## What S8 currently implements in Veldrid

Key files under `src/Veldrid/ShaderBundle/`:
- ShaderPipelineShape.cs: named slot types, reflection descriptor, Bind/Verify, typed pipeline creation, cached resource sets, eviction.
- ShaderContract.cs: shape/struct/resource/vertex checks. Old S7 attribute/registry implementation was removed.
- ShaderVertexBuffer.cs: typed vertex uploads; no public raw DeviceBuffer. Named VertexInput<T>.Bind instead of positional buffer arrays.
- ShaderUniformArena.cs: UniformArenaElement (element index, not byte offset), [DynamicUniform], typed staging/upload, device-aligned stride.
- ShapeCommandBindings.cs: unconditional draw/dispatch checks, owner identity, pipeline-selection invalidation, resource revision.
- Pipeline.cs carries internal verified-shape ownership. CommandList validates graphics draws and direct/indirect compute dispatch in Release too.

Important behaviour:
- Bind derives its descriptor from REAL C# fields; Describe is private/non-virtual. Shape must be a sealed direct subclass.
- Reassigning public slot handles fails identity checks; unbound/disposed shapes cannot create pipelines.
- UpdateMember accepts only a direct field expression on its own T, not captured objects or nested fields.
- Pipelines currently belong to an exact shape INSTANCE. Select the pipeline before binding its vertex slots or sets. Switching pipelines requires rebinding.
- Changing a resource slot requires BindResourceSets again before draw/dispatch (revision checked).
- TextureSlot.Forget(view/texture) evicts cached sets; ImGui uses it for image removal and font rebuild. Already recorded references survived eviction in D3D11/Vulkan tests.
- Dynamic arena: create from a verified [DynamicUniform] UniformBlock<T>, Stage typed values, Upload once, Select by UniformArenaElement. One arena of T can feed differently named blocks if each destination verified that same T (Silky PaintParams/GradientParams/ImagePatternParams).
- Arena uses non-Dynamic UniformBuffer and one full-buffer sequenced upload. Dynamic descriptor window is one compiled block, NOT the whole multi-MB arena. Array overloads handle zero dynamic offsets.
- Shape loading/pipeline generation still has limitations; see the audit list below. No all-bugs-closed claim is justified yet.

### ImGui

- GLSL sources: `src/Veldrid.ImGui/Shaders/Source/ImGui.{vert,frag}.glsl`.
- Bundle/snapshot committed under Shaders/Compiled. Tool: `dotnet run --project src/Veldrid.ImGui/Scripts/CompileShaders.csproj -- src/Veldrid.ImGui`.
- ImGuiShape.cs names actual inputs/resources. ImGuiRenderer.cs uses typed vertices and shape bindings; native ImDrawVert values are explicitly copied by field.
- Runtime Y-flip/colour-space flags are named uniform fields. Passing specialization constants at pipeline creation does NOT change baked DXBC; the migration initially made that mistake and corrected it.
- Offscreen tests exercise the actual shader/shape, not the entire ImGui widget renderer. Full visible-UI acceptance remains downstream.

## Silky migration: implemented and remaining

Read `C:\PROJECTS\AN_SilkyNvg\plans\typed_shader_shape_migration_IMPL.md` and `plans/paint_uniform_arena_IMPL.md`.
Code root: `src/rendering/SilkyNvg.Rendering.Veldrid/`.

Implemented:
- All 30 concrete shapes in Shaders/NvgPipelineShapes.cs and NvgPipelineVariantShapes.cs. All match committed bundles.
- ShaderLayouts stored field identifiers now match GLSL exactly. CPU-facing computed properties preserve existing paint calculations WITHOUT changing memory layout. Sizes paint/view/sweeper = 176/32/52 bytes.
- NvgPipelineFixedState contains fixed-function state + verified shape only, no manually supplied layouts/shaders.
- PipelineSet.CreateTracked and its blend variants use shape.CreateGraphicsPipeline. Main pipeline initializers/helper calls migrated; sweeper's three pipeline creation calls also migrated.
- VeldridRenderer_Shapes.cs loads one concrete typed shape per bundle and maps shader identity to that family. ShapeForShaders checks the entire shader array. All 30 load sites route through it.
- `_solidFillShape` is the same loaded family instance; it supplies the verified PaintParams for arena creation.
- Arena file moved with git mv to PaintUniformArena.cs. Original VeldridRenderer_PaintArena.cs now holds ONLY renderer telemetry.
- PaintUniformArena owns ShaderUniformArena<PaintUniforms>; GPU raw buffer/range accessors removed. Stage each CPU paint slot, upload one full segment, select by typed element.
- VeldridRenderer.Dispose calls DisposePipelineShapes; verify recovery/disposal paths as the rest is migrated.

Remaining errors (last build: 91, net9.0):

| File | Count | Main work |
|---|---:|---|
| VeldridRenderer_Pipelines.cs | 28 | old CreateResourceLayouts/CreateBuffers/resource-set construction still present |
| VeldridRenderer_BlendModes.cs | 20 | backdrop texture/set caches, uniform updates, numbered binds |
| VeldridRenderer_ClipMask.cs | 12 | mask sets and mask uniform/bind paths |
| VeldridRenderer_Flush.cs | 10 | BindPaintResourceSet still takes ResourceSet and calls removed DynamicOffsetOf; raw vertex/buffer binds |
| VeldridRenderer_Filters.cs | 8 | filter layouts/sets/pipelines and binds |
| VeldridRenderer_Sweeper.cs | 7 | layouts, texture sets, vertex buffers and binds (pipeline helper already migrated) |
| VeldridRenderer_Layers.cs | 5 | layer uniform/resource-set handling |
| TextureRegistry.cs | 1 | raw textured resource-set creation/cache |

Suggested next execution order:
- [ ] A. Replace resource-set caches with typed family slot selection; do not reintroduce an anonymous positional binding adapter.
- [ ] B. Replace BindPaintResourceSet/BindPaintIndifferentResourceSet with named block selection + shape.BindResourceSets. Respect segment residency before selecting a paint.
- [ ] C. Fan out view/text-gamma values to appropriate named family uniform blocks; preserve sequenced mid-command-list view changes during layer capture.
- [ ] D. Replace raw vertex buffers/binds with ShaderVertexBuffer<NvgVertex>/ShaderVertexBuffer<SweeperGlyphVertex>; correct pipeline-before-bind order.
- [ ] E. Finish filters/layers/clip-mask/blend/sweeper texture selection and eviction; delete unused raw layout and ResourceSet fields/helpers.
- [ ] F. Renderer builds; run full unit suites and bounded graphical golds D3D11/Vulkan, including tiny arena capacity rollover, filters, layers, text and memory gates.
- [ ] G. Commit/publish Silky only after its renderer and relevant graphical tests pass.

Critical correction: **Sweeper uses RGBA32UInt curve textures (`utexture2D`), NOT SSBOs.** Storage-buffer verification is a Veldrid gap but NOT a Silky prerequisite. This was wrongly described earlier in chat/docs; do not spend the next session implementing SSBOs to unblock Silky.

### Known migration considerations

- Preserve paint dedupe, slot 0 default, segment rollover, one upload per segment. Current conversion adds CPU staging copies; measure performance before accepting it as final.
- Three paint block names share one verified T; this is valid shared DATA, not a need for a SharedResourceSet<T> abstraction.
- View uniforms are changed mid-command-list for bounds-sized captures. Existing Silky deliberately uses non-Dynamic UBOs there; current shape-owned ordinary uniforms use Dynamic buffers. Verify/fix ordering semantics, not just sizes.
- Family pipeline variants reuse the SAME shape instance. Never select one shape's sets while another shape's pipeline is active.
- ShaderShapes and PaintArena tests LINK production declaration/arena source without compiling the renderer; they can pass while the renderer remains broken. This is intentional incremental testing, not full integration success.

## Validation evidence and reproducible commands

Run from indicated repo root; redirect complete logs to a file and retain exit codes. Do not infer success from filtered output alone.

```powershell
# AN_Veldrid
dotnet build Veldrid.sln -c Release
dotnet test src/Veldrid.ContractTests/Veldrid.ContractTests.csproj -c Release
# Last result: 41 passed, includes D3D11/Vulkan offscreen tests and typed arena selection.

# AN_SilkyNvg
dotnet test tests/unit/SilkyNvg.Tests.ShaderShapes/SilkyNvg.Tests.ShaderShapes.csproj -c Release
# Last result: 34 passed: 30 shapes + coverage/layout tests + actual SolidFill/arena readback on D3D11/Vulkan.
dotnet test tests/unit/SilkyNvg.Tests.PaintArena/SilkyNvg.Tests.PaintArena.csproj -c Release
# Last result: 14 passed (CPU production arena core).
dotnet build src/rendering/SilkyNvg.Rendering.Veldrid/SilkyNvg.Rendering.Veldrid.csproj -f net9.0
# EXPECTED currently: 91 errors; this is the migration worklist, not a passing gate.
```

Earlier all-unit sweep: 335 tests passed in 16 other projects; PaintArena was blocked then, now separately passes 14. Do not conflate runs into a newly executed all-suite result.
Logs on this machine: `C:\TEMP\silky-pipeline-shapes-build.log`, `silky-paint-readback.log`, `silky-typed-arena-tests.log`, `arena-final-tests.log`.

## After Silky: downstream chain and acceptance

- [ ] H. AN_FluidUI (highest-priority consumer): bump Silky/Veldrid/SPIRV as needed; migrate StretchBlit to typed shape; precompile/check bundles; bounded D3D11/Vulkan capture tests; publish.
- [ ] I. AN_Monsters/RenderKit: replace old S7 data-record shapes, attributes/registry and raw APIs. Keep SpaceGame building; validate `src/Monsters` gameplay with captures, not just splash. Existing LightPointStencil struct test failure predates S8.
- [ ] J. AN_ArcaneSiege: migrate shared TileMapKit, ArcaneSiege and TOMF renderers; explicit dependency floors; ordinary unit tests.
- [ ] K. ArcaneSiege 3 gates: default vertexlit, unlit review, deferred. TOMF 3 gates: world/HUD/combat. Commands/oracles in `_SPECS_TOMF/00_TOMF_IMPL_Tracking.md`.
- [ ] L. Additional ArcaneSiege visible ImGui capture: existing `--capture` hides overlays, so it is not enough to validate the GUI. Exercise text, windows, clipping, image switching. Bounded exit required.
- [ ] M. Cold/warm shader cache and deliberate bad-name/offset/missing-metadata tests. Publish/commit/push tested consumer checkpoints with exact versions.

The older six 0-pixel gates were against Veldrid 5.261031.122406 / SPIRV 5.261031.121914, BEFORE mandatory typed API changes. They do NOT validate the current S8 packages.

## Outstanding verification audit (do not claim complete safety yet)

- [ ] Bundle/result metadata is publicly mutable/constructible; audit proof provenance and immutable copies so metadata cannot diverge from loaded shader bytes.
- [ ] Final backend artifacts: SPIR-V reflection alone does not prove DXBC/MSL/driver-stage mappings. Check final-stage signatures/binding maps where required. Earlier notes asserting fxc cannot alter them were too strong.
- [ ] Uniform scalar signedness/type, matrices (orientation/stride), arrays/nested structs and actual managed-vs-marshalled offsets need complete checks or explicit fail-closed rejection.
- [ ] Current vertex compatibility checks component count and integer-vs-float, not every signedness/format case. Explicit VertexFormat must fit the actual field storage.
- [ ] ShaderInterfaceVerifier parses opaque resources but its source-vs-compiled path does not yet compare all of them. Storage member/stride reflection is absent; typed shape binding currently rejects storage buffers.
- [ ] Compute dispatch guard exists, but complete compute bundle loading/creation and reflected layouts remain to audit.
- [ ] Audit all remaining public/protected bypasses and field-handle mutation cases. Tests of known entry points are not a universal proof.
- [ ] SPIRV old ResourceFactoryExtensions still call now-internal methods. Remove/migrate those helpers when advancing its Veldrid dependency; do not grant friend access.
- [ ] Cache keys must include ALL compilation options/specializations and trustworthy compiler/native identity. Missing/corrupt interface snapshots currently need fail-closed review.
- [ ] GLES name lookups do NOT prove uniform member offsets or eliminate every alignment bug; correct earlier broad claims in design before treating them as guarantees.

## Deferred

S9 source generation; AN_VeldridUtils extraction; TOMF specs 48/49 gameplay (range abstraction, explicit skill targeting, FOV/AI). The handoff is shader migration, not gameplay.

## Start-of-next-session prompt

Read this tracking file, then AN_SilkyNvg/plans/typed_shader_shape_migration_IMPL.md. Continue Silky's 91-error typed-shape migration without reopening raw APIs. Preserve its paint arena upload model; sweeper is texture-backed, not SSBO-backed. Start with the full renderer build and the two targeted test commands above. Finish Silky before updating FluidUI, then Monsters and both ArcaneSiege targets. Source generation and utilities extraction remain deferred.