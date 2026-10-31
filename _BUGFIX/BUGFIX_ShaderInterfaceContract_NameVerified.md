# BUGFIX: Shader interface contract - every boundary verified BY NAME, declared interface never eliminated

**Status:** S1-S6 SHIPPED 2026-10-31 across AN_Veldrid / AN_VeldridSpirv / AN_SilkyNvg / AN_FluidUI / AN_Monsters / AN_ArcaneSiege (see 5.1 as-built); S7 (pipeline shapes everywhere) OPEN.  
**Owners:** `AN_VeldridSpirv` (compiler + reflection + verifier), `AN_Veldrid` (bundle format, pipeline-creation verification).  
**Consumers fixed by it:** every Veldrid user - RenderKit/Monsters/SpaceGame, FluidUI/SilkyNvg/PixelStudio, ArcaneSiege/TOMF.  
**Supersedes:** `BUGFIX_spirv-cross-hlsl-varying-packing.md` (its "Option B" becomes an enforced rule); `_BUGFIX/10_SPIRV_PreserveBindings.md`
in AN_Monsters (applied; this generalises it from bindings to the whole interface). `AN_Monsters/_SPECS_RenderKit/60_ShaderInterfaceContract.md`
keeps only the RenderKit-side consumer migration and points here.

## 1. The bug class (three production hits)

| date | shader | what happened | why nothing caught it |
|---|---|---|---|
| May 27 | Monsters `GBufferHeightmap` (D3D11) | PS omitted `frag_InstanceIndex`; fxc packed VS/PS interpolators differently; UVs constant | workaround: declare + dead-reference in the PS |
| Oct 5 -> 26 | RenderKit `ShadowDepthInstanced` | dead `Normal`/`Color` vertex inputs eliminated; per-instance attributes mis-read; no shadows for 3 weeks | bundle records layouts/bindings only |
| **Oct 31** | ArcaneSiege `ChunkInstanced` vertex-lit (D3D11) | vert gained varyings 3..5, light moved to 6; frag declared 0,1,2,6; **declaring 3..5 unused in the frag changed nothing** (optimizer strips unread inputs); fxc packed `o1 = {hasOverlay, tileUV.xy, parity}` (VS) vs `o1 = {hasOverlay, light.xyz}` (PS); every tile tinted `(u, v, rowParity)` | both stages said `TEXCOORD6` - correct BY NUMBER; sibling frags (unlit: never reads 6; GBuffer: declares+reads all) rendered fine |

Mechanism (verified Oct 31 with debug-vs-optimised HLSL dumps of the same pair): elimination happens at **GLSL -> SPIR-V** - shaderc
`shaderc_optimization_level_performance` = spirv-opt `RegisterPerformancePasses(preserve_interface = false)`; ADCE removes an input variable
that is never read. SPIRV-Cross and fxc remove nothing that reaches them. Then fxc packs each stage's struct independently in declaration
order; different sets -> different packing -> wrong data under a correct semantic name. Vulkan/Metal/GL link by explicit location and are
immune; D3D11 is the only backend that links by packed register, so it is where every hit surfaced.

## 2. Decisions

- **D0 THE PARTITION (user, 2026-10-31) - the invariant every rule below serves.** Any defect in the shader compiler, in our
  SPIRV-Cross / Veldrid.SPIRV toolchain, or in Veldrid's pipeline plumbing that would MISALIGN data - between CPU and GPU (vertex
  elements, resource slots, uniform members) or between shader stages (varyings) - MUST surface as a hard-fail exception we DETECT:
  at compile time (D3 + V4/V5/V6), at test time (S7 shapes), or at pipeline creation (V7). Never as a visual. When every gate is
  green, the only possible source of visual incorrectness is the GLSL itself and the VALUES we put into correctly-aligned slots.
  "It renders wrong" must mean "the shader is wrong" - never "the compiler moved something", never "the layout was off by one".
  Corollary: a detector must exist for every alignment the toolchain can disturb. The list today: vertex input location/format/name
  (V4, V7a); varying location/type/flat per stage and across stages (V5a, V5b); uniform block member offset/size/name (V6, V7c);
  resource set/binding/kind/name (PreserveBindings, V7b); bundle format itself (version gate). Anything added to this list later is a
  gap found, not a feature.
- **D1 ONE code path, precompiled == runtime (user).** Runtime-compiling a shader and caching it IS vdshader-precompiling it: the same
  compile, the same verification, the same `.vdshader` bytes, the same loader. The only variables are WHEN it runs and WHERE the bundle
  sits (committed `Shaders/Compiled/` + embedded resource, or a disk cache). One class does both (4.5 `ShaderBundlePipeline`); every build
  tool and every runtime source is a thin caller. Today there are FIVE copies of this pipeline - SilkyNvg `CompileShaders_Program`, FluidUI
  `CompileShaders_Program`, Monsters `CompileShaders_Program`, Monsters `RuntimeGlslShaderSource`, TileMapKit `ShaderLoader` (the last
  bypasses the bundle entirely via `CreateFromSpirv`) - which is why each bug of this class has been fixed up to five times or zero times.
  After migration `ResourceFactoryExtensions.CreateFromSpirv` is `[Obsolete(error: true)]`. No check may exist on one path and not the other.
- **D1b The most important consumer is FluidUI (user).** vdshader exists because Veldrid's precompiled shaders did not work across
  D3D11 / Vulkan / Metal before it; FluidUI (and SilkyNvg beneath it) ships on all of them from embedded bundles with NO native compiler
  at runtime (Android). Every phase below is gated on FluidUI + SilkyNvg bundles compiling, verifying and rendering BEFORE Monsters or
  ArcaneSiege are touched. Breaking FluidUI is the one unacceptable outcome.
- **D2 The NAME rule (user).** A slot number - location, binding, std140 offset, register - is never evidence of agreement. The join key at
  every boundary is the **name**; the numbers are verified AGAINST the names. A name on one side and not the other, or on both at different
  slots/types, is an exception. Never a warning.
- **D3 Declared interface is never eliminated.** `CompileGlslToSpirv` preserves interface variables through optimisation (see 4.1). Declared ==
  compiled by construction; V4/V5a (below) is the regression guard that proves it stays so.
- **D4 Vert-out == frag-in, exactly.** Identical varying sets by (name, location, scalar type, vecsize, flat). This makes the D3D11 packing
  class IMPOSSIBLE (identical structs pack identically) rather than merely detected. Authoring consequence: a fragment shader declares every
  vertex output even when unused; with D3 the declaration survives, and the error names what is missing.
- **D5 The bundle carries its interface.** `.vdshader` gains an `interface` section (4.3); bundles without it are rejected (format version bump).
- **D6 Pipeline creation verifies, in Veldrid.** `ResourceFactory.CreateGraphicsPipeline` / `CreateComputePipeline`, for any `Shader` created
  by `CreateFromBundle`, verifies the description against the bundle interface by name (V7). This protects every consumer, not only ones that
  route through RenderKit's `PipelineCache`.
- **D7 C# uniform structs are part of the contract.** `[VdShaderUniformBlock(bundleName, blockName)]` moves from RenderKit into Veldrid
  (`Veldrid.ShaderBundle`); V7c compares field NAME + `Marshal.OffsetOf` + size to the compiled block member. RenderKit's attribute becomes a
  forwarder then is deleted.

## 3. Rules

| rule | boundary | join key | verified | where | exception |
|---|---|---|---|---|---|
| V4 | GLSL `in` (vertex) declared <-> compiled vertex inputs | name | location, format | compile | `ShaderInterfaceContractException` |
| V5a | GLSL `out`/`in` (varyings) declared <-> compiled, per stage | name | location, type, flat | compile | same |
| V5b | compiled vert outputs <-> compiled frag inputs | name | IDENTICAL sets: location, type, flat | compile | same |
| V6 | GLSL block members declared <-> compiled block members | name | std140 offset, size; block size | compile | same |
| V7a | `VertexLayoutDescription` elements <-> bundle vertex inputs | `VertexElementDescription.Name` | flattened slot order -> location, format | pipeline creation | `ShaderContractException` |
| V7b | `ResourceLayoutDescription` elements <-> bundle resources | element name | set, binding, kind, stages | pipeline creation | same |
| V7c | `[VdShaderUniformBlock]` struct fields <-> bundle block members | field name | `Marshal.OffsetOf`, size; struct size == block size | pipeline creation | same |

Binding numbers were already protected (PreserveBindings); V7b adds the name so a renamed/swapped resource at the same slot is caught.

## 4. Design

### 4.1 D3 in `libveldrid-spirv.cpp CompileGlslToSpirv` (S2, first task - O1 decides)
Preferred: let shaderc produce UNoptimised SPIR-V, then run `spvtools::Optimizer` ourselves with `RegisterPerformancePasses(/*preserve_interface*/ true)`
(SPIRV-Tools is already linked through shaderc; needs `spirv-tools/optimizer.hpp` on the include path - check `ext/known_good.json` / CMake).
Bindings stay preserved (ADCE with preserve_interface keeps interface vars; `SetPreserveBindings` semantics are kept by passing the flag through
to the passes that renumber). Fallback if headers are unavailable: no SPIR-V-level optimisation at all (fxc O3 and the drivers optimise
anyway; Vulkan drivers optimise SPIR-V on `vkCreateShaderModule`). Either way the test is: optimised and debug compiles of a frag with an
unread input have the same `FragmentInputs`.

### 4.2 Reflection (native -> C#) in `CompileVertexFragment` / `CompileCompute`
Both SPIRV-Cross compilers are in hand (lines 345-346). Read BEFORE any renaming (GLSL targets rename varyings to `vdspv_fsinN`, O4):
- varyings: `vsResources.stage_outputs`, `fsResources.stage_inputs` -> name (`get_name`, fallback name), `DecorationLocation`, `SPIRType.basetype/vecsize/columns`, `DecorationFlat`;
- vertex inputs: already `ReflectVertexInfo` (name, location, format) - keep;
- uniform blocks: per `uniform_buffers` resource -> block name, set, binding, `get_declared_struct_size`, members via `get_member_name`,
  `type_struct_member_offset`, `get_declared_struct_member_size`, GLSL type string;
- resources: already (`CreateResourceLayoutArray` - names present).
New interop: `NativeVaryingDescription`, `NativeUniformBlockDescription`, `NativeUniformMemberDescription`; `ReflectionInfo` gains three arrays;
`NATIVE_ABI_VERSION` bump; `Veldrid.SPIRV.AbiCheck` updated.

### 4.3 Types (branded; C# in `Veldrid.SPIRV`, JSON mirror in `Veldrid.ShaderBundle`)
```csharp
public enum ShaderInterface_ScalarType { Float, Int, UInt, Bool }
public readonly record struct ShaderInterface_Varying(string Name, uint Location, ShaderInterface_ScalarType Scalar, uint VecSize, uint Columns, bool Flat);
public readonly record struct ShaderInterface_VertexInput(string Name, uint Location, VertexElementFormat Format);
public readonly record struct ShaderInterface_UniformMember(string Name, uint Std140OffsetBytes, uint Std140SizeBytes, string GlslType);
public sealed record ShaderInterface_UniformBlock(string BlockName, uint Set, uint Binding, uint Std140SizeBytes, ShaderInterface_UniformMember[] Members, ShaderStages Stages);
public sealed record ShaderInterface_Resource(string Name, uint Set, uint Binding, ResourceKind Kind, ShaderStages Stages);
public sealed record ShaderBundleInterface(ShaderInterface_VertexInput[] VertexInputs, ShaderInterface_Varying[] VertexOutputs,
    ShaderInterface_Varying[] FragmentInputs, ShaderInterface_UniformBlock[] UniformBlocks, ShaderInterface_Resource[] Resources);
public readonly record struct ShaderContractViolation(string Rule, string Name, string SideA, string SideB, string Detail);   // "V5b", "fragTileUV", "vertex out location 3 vec2", "(absent)", ...
public sealed class ShaderInterfaceContractException : SpirvCompilationException { ShaderContractViolation[] Violations; }   // compile-time (AN_VeldridSpirv)
public sealed class ShaderContractException : VeldridException { ShaderContractViolation[] Violations; }                        // pipeline-time (AN_Veldrid)
```
`SpirvReflection.Interface`, `VeldridShaderBundle.Interface` (JSON `"interface"`), `PrecompiledShaderResult.Interface`. The message lists
every violation as a declared-vs-compiled table - one glance tells you which name, which side, which slot.

### 4.4 V7 in Veldrid (D6)
`CreateFromBundle` registers each created `Shader` -> `ShaderBundleInterface` in a `ConditionalWeakTable` on the `GraphicsDevice`
(`ShaderInterfaceRegistry`). `ResourceFactory.CreateGraphicsPipeline(ref desc)`: if any `desc.ShaderSet.Shaders` is registered, run
`ShaderContractVerifier.VerifyGraphicsPipeline(ref desc, interface)` (V7a: flatten `VertexLayouts` in slot order to locations, compare name +
format; V7b: `ResourceLayout.Description` elements per (set index, binding index) vs `Resources`; V7c: every type carrying
`[VdShaderUniformBlock(bundle, block)]` for this bundle name - discovered once per assembly via `ShaderUniformBlockRegistry.Register(Assembly)`
called by the consumer at startup, or by scanning loaded assemblies lazily). Throws before any native call. Shaders not from a bundle (legacy
`CreateFromSpirv`, raw bytecode) are not verified - and D1 removes those paths.

### 4.5 `ShaderBundlePipeline` - the ONE compile -> verify -> serialize -> cache -> load path (D1)
Lives in `Veldrid.SPIRV` (it needs the native compiler); the load half is `Veldrid.ShaderBundle` (`CreateFromBundle`), usable without the
native library (Android, embedded bundles). Build tools and runtime sources are thin callers of the SAME methods:
```csharp
public readonly record struct ShaderBundle_SourcePair(string BundleName, string VertexFile, string FragmentFile);   // or ComputeFile
public readonly record struct ShaderBundle_InputHash(string Hex);        // sha256(bundleName | normalised sources | compiler version tag | format version)

public static class ShaderBundlePipeline
{
    // The compile: GLSL -> SPIR-V (D3 preserved) -> all backends -> V4/V5/V6 verified -> VeldridShaderBundle with Interface. THROWS ShaderInterfaceContractException.
    public static VeldridShaderBundle BuildBundle(string vertexGlsl, string fragmentGlsl, string bundleName, string vertexFile, string fragmentFile, CrossCompileOptions? options = null);
    public static ShaderBundle_InputHash ComputeInputHash(string bundleName, params string[] normalisedSources);
    // The cache: one implementation for Shaders/Compiled (committed, build tool) and %LOCALAPPDATA% (runtime). Hash manifest file beside the bundles
    // (today's shaderinputhashes.manifest). Returns the bundle file path; rebuilds when the hash differs or the bundle lacks the current format/interface.
    public static string GetOrBuild(string sourceDir, string cacheDir, ShaderBundle_SourcePair pair, ShaderBundle_BuildLog? log = null);
    public static int GetOrBuildAll(string sourceDir, string cacheDir, IReadOnlyList<ShaderBundle_SourcePair> pairs, bool deleteOrphans, ShaderBundle_BuildLog? log = null);  // = every CompileShaders_Program
}

// Runtime: the same cache + CreateFromBundle, owning the Shaders. Replaces Monsters RuntimeGlslShaderSource and TileMapKit ShaderLoader.
public sealed class ShaderBundleRuntimeSource : IDisposable
{
    public ShaderBundleRuntimeSource(GraphicsDevice device, string sourceDir, string cacheDir, IReadOnlyDictionary<string, ShaderBundle_SourcePair> known);
    public PrecompiledShaderResult Get(string bundleName);     // GetOrBuild -> Deserialize -> factory.CreateFromBundle (registers the interface for V7)
}
```
- `ShaderBundle_InputHash` folds in `typeof(ShaderBundleCompiler).Assembly` version AND `VeldridShaderBundle.FormatVersion`: a compiler or
  format change invalidates every cache on every machine at once (Monsters already did the former; FluidUI's manifest did neither).
- Build-tool behaviour preserved: committed outputs, incremental by content hash, orphan deletion, non-zero exit on failure. The three
  `CompileShaders_Program.cs` files shrink to: parse args -> pairs -> `GetOrBuildAll` -> exit code.
- `EmbeddedShaderBundleSource` (RenderKit) and `StretchBlitRenderer` (FluidUI) keep loading embedded bundles via `CreateFromBundle`; they
  change only in that `CreateFromBundle` now registers the interface and pipeline creation verifies it (V7).
- A bundle produced at build time and one produced at runtime from the same sources are byte-identical (the compiler is already bit-reproducible:
  no timestamps) - the test for D1 is literally `File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b))`.

### 4.6 What stays per-consumer
Only: WHICH pairs exist (a list), WHERE sources live, WHERE the cache lives, and how a `Shader[]` reaches a pipeline description. Nothing
about compiling, hashing, verifying, serialising, loading or validating is written outside AN_Veldrid / AN_VeldridSpirv ever again.

## 5. Phases (each ends with `cmd/publish-local` + the downstream Build.props bump)

Publish chain (every arrow = `cmd/publish-local` upstream, then bump the named `*.Build.props` floor downstream, restore, build, gate):
```
AN_Veldrid  ->  AN_VeldridSpirv (Veldrid floor)  ->  AN_SilkyNvg (SilkyNvg.Rendering.Veldrid.Build.props)  ->  AN_FluidUI (ANVeldridVersion, ANVeldridSPIRVVersion, ANSilkyNvgVersion)
            ->  AN_Monsters (Monsters.Build.props: ANFluidUIVersion carries Veldrid; ANVeldridSPIRVVersion)  ->  AN_ArcaneSiege (ArcaneSiege.Build.props: all three)
```
Gates, in priority order (D1b): **G-Fluid** = SilkyNvg + FluidUI `CompileShaders` produce bundles that pass V4-V6, AND the FluidUI
GameCompositor test (`cmd\gtest.bat` D3D11, `cmd\vk-gtest.bat` Vulkan) + the SilkyNvg Veldrid sample render as before; **G-Monsters** =
`AN.RenderKit.Tests`, Monsters + SpaceGame launch D3D11 + Vulkan; **G-Arcane** = ArcaneSiege gates (1)(2)(3) = 0 px, TOMF v3 oracles = 0 px.
The Oct 31 bug is the acceptance test for the whole thing: gate 1 must go from 917020 px to 0 px with NO shader workaround, only the rule.

**S1 - AN_VeldridSpirv: reflect + SCREAM (V5b, V7-ready interface) + the one pipeline (4.5)** - [x]
- [x] native 4.2 reflection + ABI bump; C# 4.3 types; `SpirvReflection.Interface`.
- [x] `ShaderInterfaceVerifier.VerifyStages(interface)` (V5b) called inside `ShaderBundleCompiler.CompileVertexFragment`; throws `ShaderInterfaceContractException`.
- [x] `ShaderBundlePipeline` (4.5): `BuildBundle` (compile+verify+serialize) and `GetOrBuild` (content-hash cache, both build-time and runtime); `ShaderBundleRuntimeSource` over it.
- [x] `Veldrid.SPIRV.Tests`: ArcaneSiege `ChunkInstanced` vert + vertex-lit frag as fixture -> throws naming `fragTileUV`, `fragTileRowParity`, `fragGridTile`; matched pair passes; same names at swapped locations throws; flat mismatch throws.
- [x] GATE: the fixture throws. **We know.** AND SilkyNvg's + FluidUI's existing GLSL compiled through the new compiler: every pair must pass V5b
      or the failure must be a REAL latent mismatch we then fix in the GLSL (record each in 6. as evidence).

**S2 - AN_VeldridSpirv: D3 preserve interface; V4 / V5a / V6 declared-vs-compiled** - [x]
- [x] O1 answered; 4.1 implemented; test: debug == optimised `FragmentInputs` for a frag with an unread input.
- [x] GLSL declaration parser (per stage: `layout(location = N) [flat] in|out TYPE name;`, `uniform Block { members };` after `#define`-free preprocessing) -> V4/V5a/V6 in the compiler.
- [x] GATE: the ArcaneSiege frag with 3..5 declared-unused now compiles with V5b passing; HLSL dump shows TEXCOORD3..5 in the PS struct.
- [x] publish-local AN_VeldridSpirv (interim, against the current Veldrid) so G-Fluid can run early on the compile-side checks.

**S3 - AN_Veldrid: bundle interface + V7 at pipeline creation** - [x]
- [x] `VeldridShaderBundle.Interface` + JSON context + format version bump (old bundles rejected with a clear message); `PrecompiledShaderResult.Interface`.
- [x] `ShaderInterfaceRegistry`, `ShaderContractVerifier`, `VdShaderUniformBlockAttribute`, `ShaderUniformBlockRegistry`; hooks in `ResourceFactory.CreateGraphicsPipeline` / `CreateComputePipeline`.
- [x] tests: synthetic descriptions - renamed vertex element, swapped resource names at same slots, reordered struct field, wrong struct size -> each throws with the right rule id.
- [x] publish-local AN_Veldrid; bump AN_VeldridSpirv's Veldrid dependency; publish-local AN_VeldridSpirv.

**S4 - AN_SilkyNvg + AN_FluidUI: the primary consumer (G-Fluid)** - [x]
- [x] SilkyNvg: Build.props bump; `Scripts/CompileShaders_Program` -> thin call into `ShaderBundlePipeline.GetOrBuild` (hash manifest semantics preserved);
      recompile all bundles (format bump); fix any GLSL that V4-V6 rejects; sample renders D3D11 + Vulkan; publish-local.
- [x] FluidUI: Build.props bumps (Veldrid, SPIRV, SilkyNvg); `FluidUI.GameCompositor/Scripts/CompileShaders_Program` -> thin; `StretchBlit.vdshader`
      recompiled; `StretchBlitRenderer` unchanged in shape (`CreateFromBundle` now registers the interface, V7 runs at its pipeline creation);
      `cmd\gtest.bat`, `cmd\vk-gtest.bat`, verifyall as the repo's own gates dictate; publish-local.
- [x] Android: bundles are consumed with no native compiler - confirm the loader path has no new native dependency (interface is JSON in the bundle).

**S5 - AN_Monsters: consumers adopt (spec 60 keeps this list)** - [x]
- [x] `Monsters.Build.props ANVeldridSPIRVVersion` bump; `Scripts/CompileShaders` inherits V4-V6; fix every failing bundle (the `_unused`/dead-reference hacks become plain declarations).
- [x] `Scripts/CompileShaders` and `EmbeddedShaderBundleSource` -> thin over 4.5; Monsters `RuntimeGlslShaderSource` DELETED in favour of `ShaderBundleRuntimeSource`
      (an `IShaderSource` adapter of a few lines stays in RenderKit); `[ShaderUniformBlock]` -> `[VdShaderUniformBlock]`; `ShaderBundleContractAssert`
      V3 test kept as the fast lane; `ShaderUniformBlockRegistry.Register` at startup.
- [x] GATES: G-Monsters.

**S6 - AN_ArcaneSiege / TileMapKit: the runtime path joins** - [x]
- [x] `ArcaneSiege.Build.props` bumps; TileMapKit `ShaderLoader` -> thin adapter over `ShaderBundleRuntimeSource` (cache under `%LOCALAPPDATA%/ArtificialNecessity/ShaderCache/<app>`);
      `CreateFromSpirv` gone (grep gate across all repos), then `[Obsolete(error: true)]` in AN_Veldrid.SPIRV.
- [x] `ChunkInstanced.frag` / `_Unlit.frag` keep the full declarations (now the rule, comments updated to cite this spec); delete `ShaderCrossCompileDumpTests`; `_SPECS_TOMF/10` Phase 3b closed by D1.
- [x] GATES: G-Arcane; negative test: a frag with a missing varying fails at LAUNCH with the table.

### 5.1 As built, 2026-10-31 (S1-S6 DONE; S7 open)

Everything above shipped the same day, in the chain order, each repo pushed. Deltas from the plan worth knowing:
- **D3 was option 1**: shaderc at `optimization_level_zero`, then libveldrid-spirv runs `spvtools::Optimizer` with the pinned
  `RegisterPerformancePasses()` list and `CreateAggressiveDCEPass(preserve_interface = true)`; bindings preserved via `OptimizerOptions`.
  Pinned by `D3_DeclaredInterfaceSurvivesOptimisation_DebugAndOptimisedAgree`.
- **Block size** reflected is the std140 BLOCK size (declared size rounded to 16), not SPIRV-Cross's declared struct size (68 vs 80).
- **V8 (new): `<bundle>.interface.json` snapshot** beside each bundle with a source-only hash; same sources + different compiled
  interface/layouts = toolchain drift = `ShaderInterfaceContractException [V8]`. Commit the snapshots with the bundles.
- **V7c padding rule**: C# `_pad*` fields are legal only in the std140 tail after the last member and inside the block size.
- **S7 (new): `ShaderPipelineShape` + `[VdShaderPipelineContract]` + `ShaderPipelineContractRegistry.VerifyAll`** - the C# side of a
  pipeline as data, verified against the bundle in a unit test (no device); renderers build their description FROM the shape. Done for
  RenderKit `ShadowPassRenderer` / `ShadowMapInstancedMeshRenderer` with a burn-down test pinning the 20 kit bundles still without shapes.
- **ShaderBundle_SourceReader** hook: how a GLSL file is read/expanded is the one per-consumer policy (RenderKit's transitive include
  expander plugs in); the pipeline also supports one-level `//#include` itself.
- **Found by the contract, fixed in GLSL** (evidence for 6.): ArcaneSiege `ChunkInstanced_GBuffer.frag` declared-but-unread
  `fragVertexLightColor` (worked only because it was the LAST location); RenderKit `ShadowDepth.frag` declared `AlbedoSampler` at
  set 1 binding 1 where the bound material set has `NormalTexture` (worked only by per-kind register counters) - now `AlbedoTexture` @0 +
  `MaterialSampler` @4 under the material set's names.
- **Acceptance**: ArcaneSiege gate 1 917020 px -> 0 px; gates 2, 3 and TOMF v3/v3/v2 = 0 px; G-Fluid D3D11 + Vulkan; SilkyNvg sample
  D3D11 + Vulkan; Monsters + SpaceGame clean. Versions: Veldrid 5.261031.122406, Veldrid.SPIRV 5.261031.121914, FluidUI 0.261031.115758,
  SilkyNvg 1.261031.115316.
- **Not done** (carried into S7): `CreateFromSpirv` `[Obsolete(error: true)]`; `ShaderUniformBlockRegistry.Register(assembly)` at app
  startup (today V7c covers only shape-listed / attributed structs); `CreateComputePipeline` hook (abstract per backend); RenderKit's legacy
  `[ShaderUniformBlock]` + `KitUniformStructs_MatchKitManifest` (pre-existing failure: `LightPointStencil.LightTransform` has no struct);
  Monsters `Compiled/` is gitignored so its snapshots are not versioned.

**S7 - every pipeline is a declared shape, verified at TEST time** - [ ]
- [ ] RenderKit: shapes for the 20 remaining bundles (`DeferredLightingPass` x6, `MeshRenderer`, `InstancedMeshRenderer`, `DebugBufferOverlay` x3, `DebugLineRenderer`, `SkyboxRenderer`, `GameRenderModeSplashImage`, `AtmosphereFlowSimulation` x6, `AtmosphereSphereRenderer`); burn-down list -> empty.
- [ ] Monsters (5 runtime bundles; test resolver = `ShaderBundlePipeline.GetOrBuild` over `Assets/Shaders` - first "runtime shaders verified at test time"); SpaceGame (3).
- [ ] TileMapKit `TileRenderer_ChunkInstanced` (3 modes), `TileBatch`; ArcaneSiege `SpriteBatch`, `ParallaxBackgroundRenderer`, debug renderers; TOMF `TomfBillboardRenderer`; kit test with the compile-at-test resolver.
- [ ] SilkyNvg pipeline families (30 bundles, shared layouts -> parameterised shapes); FluidUI `StretchBlitRenderer`.
- [ ] Then the hardening: per-app `ShaderUniformBlockRegistry.Register`; `CreateFromSpirv` `[Obsolete(error: true)]`; analyzer forbidding `new GraphicsPipelineDescription` outside `ToGraphicsPipelineDescription` (last, once lists are empty).

### 5.2 S8 - SUPERSEDED by 5.3 (D10) the same day - kept for the reasoning; D8/D9 and the GLES finding still hold: a GLSL NAME at every binding; the name-free APIs are deleted (D8)

**Why S7 is not enough (user).** S7 verifies pipelines that declared a shape; V7c verifies structs that were attributed or listed.
Both are opt-ins, and an opt-in is not "no way". Worse, the check sits at pipeline creation while the bytes reach the GPU through
APIs that never see a name: `CommandList.UpdateBuffer(buffer, offset, value)` writes a `Matrix4x4` at byte 0 of a buffer bound to
`uniform MVPBuffer { mat4 MVP; }` without ever saying `MVP`; `new ResourceSetDescription(layout, buf, tex, smp)` is positional. Every
GLSL variable HAS a name; a C# API that binds to it without that name is the defect. We own the fork: the name-free APIs go.

- **D8 A GLSL name is required at every binding point, and the name is checked there.** No API exists by which bytes reach a shader
  uniform block member, texture, sampler, storage buffer or image slot without the GLSL name being supplied and verified against the
  compiled bundle. Vertex buffers are covered by vertex ELEMENT names (V7a). Deleting the positional/offset APIs makes a half-migrated
  tree fail to COMPILE, not render wrong.
- **D9 The NAME is the type (user, 2026-10-31).** The structural types at a shader boundary are degenerate: `Position`, `Normal`,
  `Color`, `TexCoord` are all `vec3`/`vec4`; `Model` and `LightViewProjection` are both `mat4`. Every misalignment we have had was
  TYPE-CORRECT - the bytes fit, the meaning was swapped. Locations, bindings, offsets and registers are ADDRESSES, not types; a vec4 at
  the wrong address type-checks. The only token in the whole pipeline that carries MEANING is the identifier the author wrote. So the
  verification space is the set of GLSL identifiers, and every rule in this spec reduces to one sentence: *the thing named X on the C#
  side is bound to the thing named X in the GLSL, at every link, or it is an exception.* Structural checks (component count, size,
  std140 offset) are secondary - they catch a wrong struct for the right name; they can never catch the right struct for the wrong name.
- **GLES is the existence proof, not the exception.** `OpenGLPipeline.cs` has ALWAYS bound by name - `glBindAttribLocation(slot,
  VertexElementDescription.Name)`, `glGetUniformBlockIndex(ResourceLayoutElementDescription.Name)`, `glGetUniformLocation(name)`,
  `glGetProgramResourceIndex(name)` - and a wrong name THROWS ("There was no attribute variable with the name X"). This bug class was
  impossible on GL. D3D11 / Vulkan / Metal bind by number, so the same mismatch was silent there. Veldrid.SPIRV papered over GL's name
  requirement by SYNTHESISING names from numbers (`NormalizeResourceNames` -> `vdspv_{set}_{binding}`, varyings -> `vdspv_fsin{N}`,
  libveldrid-spirv.cpp 90-118 / 416-428) - i.e. it degraded the one backend that had the contract right. S8 therefore: (a) the real GLSL
  names flow to every backend (normalisation and `vdspv_*` renaming removed; the fork already preserves uniform block names on GL), and
  (b) GL's existing name lookups are the REFERENCE behaviour the new link-2/link-3 checks reproduce on the other three backends.

**The chain (each link by name, each verified where it is formed):**
```csharp
// Link 1 - struct <-> block, at buffer creation. T's fields: NAME == member name, Marshal.OffsetOf == std140 offset, size == size; sizeof(T) == block size.
public sealed class UniformBlockBuffer<T> : IDisposable where T : unmanaged
{
    // created ONLY from a verified bundle result; throws ShaderContractException on any mismatch
    internal UniformBlockBuffer(PrecompiledShaderResult bundle, string glslBlockName, ResourceFactory factory, BufferUsage extraUsage = 0);
    public string BlockName { get; }                                   // "Transforms"
    public VdShaderUniformBlock CompiledBlock { get; }
    public void Update(CommandList cl, in T wholeBlock);                 // the only whole-block write
    public void Update<TMember>(CommandList cl, string glslMemberName, in TMember value) where TMember : unmanaged;   // member exists, sizeof(TMember) == member size, written at the COMPILED offset
    // NO DeviceBuffer exposed, NO offset parameter anywhere: UpdateBuffer(buffer, offset, ...) cannot reach it.
}
public static UniformBlockBuffer<T> CreateUniformBlockBuffer<T>(this PrecompiledShaderResult bundle, ResourceFactory factory, string glslBlockName, BufferUsage extraUsage = 0);

// Link 2 - resource <-> layout element, at resource-set creation. Each name must be an element of the layout; each element filled exactly once;
// a UniformBuffer element accepts ONLY a UniformBlockBuffer whose BlockName == the element name; storage buffers/images likewise by name.
public ResourceSetDescription(ResourceLayout layout, params (string GlslName, BindableResource Resource)[] bindings);
// the positional ResourceSetDescription(layout, params BindableResource[]) is DELETED.

// Link 3 - layout element <-> shader resource, at pipeline creation (V7b, exists) + vertex element <-> shader input (V7a, exists)
// + shaders not from a bundle REFUSED + compute hooked. ShaderUniformStructs on the description is NOT needed: link 1 proves struct<->block.
```
- `[VdShaderUniformBlock]`, `ShaderUniformBlockRegistry`, `ShaderPipelineShape.UniformStructs` and the just-added
  `GraphicsPipelineDescription.ShaderUniformStructs` are REMOVED - the string at link 1 is stronger than any attribute (it cannot be
  forgotten; there is no untyped path). `ShaderPipelineShape` keeps vertex layouts + layout descriptions (S7 still verifies those at test time).
- `CommandList.UpdateBuffer` keeps working for vertex/index/staging buffers; a `DeviceBuffer` created with `BufferUsage.UniformBuffer`
  directly (not through link 1) is refused by `ResourceSetDescription` at link 2, so it can never be bound to a shader.
- Storage buffers / storage images: same pattern - `StorageBlockBuffer<T>` / named slot binding; typed where the GLSL declares a struct.
- Textures and samplers: bound by element name at link 2; kind is checked against the layout element (TextureReadOnly vs Sampler).

**Consequence (intended):** every `UpdateBuffer`-of-a-uniform and every `ResourceSetDescription` in RenderKit, Monsters, SpaceGame,
SilkyNvg, FluidUI, TileMapKit, ArcaneSiege and TOMF must change. All of it is caught by the COMPILER because the old overloads no longer
exist; nothing can be half-migrated and still start. Order: AN_Veldrid (links 1-3 + tests) -> publish -> SilkyNvg -> FluidUI (D1b gate)
-> Monsters/SpaceGame -> ArcaneSiege/TOMF.

**S8 checklist** - [ ]
- [ ] AN_Veldrid: `UniformBlockBuffer<T>`, `CreateUniformBlockBuffer`, named `ResourceSetDescription` (positional deleted), link-2 verification
      (names, kinds, block-name match, exactly-once), link-3: refuse non-bundle shaders, `CreateComputePipeline` hook (abstract -> Core), delete
      `[VdShaderUniformBlock]` / `ShaderUniformBlockRegistry` / `UniformStructs`; ContractTests for each link incl. member-offset write; publish.
- [ ] AN_VeldridSpirv: nothing structural (bundle interface already carries members); bump floor; publish.
- [ ] SilkyNvg: migrate pipeline families + uniform writes; sample D3D11 + Vulkan; publish.
- [ ] FluidUI: `StretchBlitRenderer`; gtest / vk-gtest; publish.
- [ ] Monsters/RenderKit/SpaceGame: every renderer; `Matrix4x4_ColumnMajor` writes become `Update(cl, in struct)`; RenderKit tests.
- [ ] ArcaneSiege/TileMapKit/TOMF: `TileRenderer_ChunkInstanced`, `TileBatch`, `SpriteBatch`, parallax, debug, `TomfBillboardRenderer`; all six gates 0 px.
- [ ] Spec: D8 promoted from PROPOSED; D0's detector list gains "binding without a name (impossible by API)".

**Open for the user:** O7 - member-level `Update(cl, "Name", in value)` vs whole-struct only? (Proposal: both; member-level is how today's
single-matrix writes migrate with the least churn and the most safety.) O8 - do push-constant-style per-draw updates need a batching
helper so member-level writes do not multiply `UpdateBuffer` calls? (Measure after migration.)

### 5.3 S8 FINAL - D10: the pipeline shape is a C# TYPE whose field names ARE the GLSL names (user, 2026-10-31)

**Why this beats 5.2.** A string at every call is checked at RUNTIME, at every call site, and can be typo'd. A C# type is checked at
COMPILE time by the identifier and ONCE at construction against the shader; an instance that exists is itself the proof the check
passed. It also deletes more hand-written data (vertex layouts, resource layouts, set descriptions) instead of renaming it.

- **D10 By construction, in two levels.** (1) `ShaderPipelineShape.Bind<TShape>(bundleResult, factory)` takes the shape's DESCRIPTOR
  (every field: NAME == GLSL resource / block / vertex-input name; TYPE == kind, plus struct members by name/offset/size for blocks and
  vertex inputs), checks it against the bundle BOTH directions (a GLSL resource with no field, or a field with no resource, fails) and
  is the ONLY way to obtain an instance. (2) Renderer code touches slots only through the fields (`shape.AlbedoTexture.Set(view)`,
  `shape.ShadowTransforms.Update(cl, in v)`): a misspelt name is a C# compile error; a renamed GLSL variable fails at Bind naming the
  orphan. No string and no number anywhere in renderer code. The C# field name equals the GLSL name NOT because two strings are compared,
  but because the field is the sole handle and the sole handle exists only after level 1 passed.
- **Identifier rule:** the GLSL name must be a legal C# identifier, matched EXACTLY (case-sensitive). No attribute override - an override
  is a string that can drift. A shader whose names are not legal C# identifiers fails at Bind with "rename the GLSL variable".

```csharp
// One PARTIAL class per pipeline (partial so a generator can supply the plumbing - 5.4). Field names are the GLSL identifiers.
public sealed partial class ShadowDepthShape : ShaderPipelineShape
{
    public VertexInput<MeshVertex_PNCT>   Vertices;          // MeshVertex_PNCT's fields are the GLSL `in` names -> VertexLayoutDescription GENERATED (V7a by construction)
    [PerInstance] public VertexInput<MeshInstanceData> Instances;   // step rate 1; slot order = field order
    public UniformBlock<ShadowTransforms> ShadowTransforms;  // block by field name; struct members by name/offset/size (V7c by construction)
    public TextureSlot                    AlbedoTexture;     // set/binding come from the bundle, never from C#
    public TextureSlot                    NormalTexture;
    public TextureSlot                    SpecularTexture;
    public TextureSlot                    EmissiveTexture;
    public SamplerSlot                    MaterialSampler;
    public UniformBlock<MaterialParams>   MaterialParams;
    // also StorageBufferSlot<T>, StorageImageSlot; compute shapes have no VertexInput
}

public abstract class ShaderPipelineShape
{
    protected abstract ShapeDescriptor Describe();          // the verification INPUT as data: built by reflection (5.3) or by generated code (5.4)
    public static TShape Bind<TShape>(PrecompiledShaderResult bundle, ResourceFactory factory) where TShape : ShaderPipelineShape, new();   // THE constructor; throws ShaderContractException
    public static IReadOnlyList<ShaderContractViolation> Verify<TShape>(VeldridShaderBundle bundle) where TShape : ShaderPipelineShape, new(); // test time, no device
    public Pipeline CreateGraphicsPipeline(ResourceFactory f, BlendStateDescription, DepthStencilStateDescription, RasterizerStateDescription, PrimitiveTopology, OutputDescription);
    public Pipeline CreateComputePipeline(ResourceFactory f, uint x, uint y, uint z);
    public void BindResourceSets(CommandList cl);            // sets built from the slots by the shape; a slot never Set() throws here
    public void BindVertexBuffers(CommandList cl, DeviceBuffer vertices, DeviceBuffer instances = null);   // slot order = field order
}
public sealed class UniformBlock<T> where T : unmanaged { public void Update(CommandList cl, in T value); public void UpdateMember<TM>(CommandList cl, Expression<Func<T, TM>> member, in TM value) where TM : unmanaged; }
public sealed class TextureSlot { public void Set(TextureView view); }   public sealed class SamplerSlot { public void Set(Sampler sampler); }
public sealed class VertexInput<TVertex> where TVertex : unmanaged { }  // layout from TVertex fields: name, format from field type, offset from Marshal.OffsetOf (or generated)
public sealed record ShapeDescriptor(/* ordered slots: FieldName, Kind, StructType, Members(name, offset, size), PerInstance */);
```

**What becomes internal (no public path around the shape):** `ResourceLayout` creation from hand-written descriptions, `ResourceSetDescription`,
`VertexLayoutDescription` authoring for shader pipelines, `GraphicsPipelineDescription` / `ComputePipelineDescription` construction with
shaders, `CommandList.UpdateBuffer` on a uniform buffer (the `DeviceBuffer` behind a `UniformBlock<T>` is not exposed). Shaders not from a
bundle are refused. `ResourceFactory.CreateFromSpirv`, `[VdShaderUniformBlock]`, `ShaderUniformBlockRegistry`, the S7 data-record shape and
`GraphicsPipelineDescription.ShaderUniformStructs` are DELETED. Vertex/index/staging buffers keep `UpdateBuffer`. Backends are untouched:
the shape produces the same ordered arrays they consume today.

**Test time:** `Verify<T>(bundle)` is Bind without GPU objects. A per-assembly test enumerates every `ShaderPipelineShape` subclass (the
base class is the marker - nothing to forget) and verifies each against its bundle (embedded, or compiled at test time through
`ShaderBundlePipeline` for runtime GLSL); a second test lists bundles with no shape.

**GL:** the shape's field names are what `OpenGLPipeline` already looks up by name; D3D11/Vulkan/Metal get set/binding/location from the
bundle. One source of truth; GL was the existence proof (5.2).

**Open (D10):** O9 - shared sets: GBuffer and ShadowDepth bind the SAME material `ResourceSet`; both shapes declare the same slot fields and a
set created through one shape may be bound through another IF the layouts are identical by name (checked at `BindResourceSets`).
O10 - dynamic uniform offsets (`DeviceBufferRange`): `UniformBlock<T>.UpdateAt(cl, index, in T)` + `BindResourceSets(cl, dynamicIndex)`;
audit who uses them during migration.

**Remaining meaning boundaries (user, 2026-10-31) - decided:**
- **Which bundle: closed.** `[ShaderBundle("ShadowDepth")]` on the shape class - comment documentation turned into checkable data; `Bind`
  asserts `bundle.ShaderName` equals it. One shape <-> one shader set.
- **Shared sets across pipelines (O9): NOT a type.** GBuffer and ShadowDepth bind the same material set. Both shapes declare the same slot
  fields (retyping is a feature - each shape is a complete statement of its shader), and `BindResourceSets` checks an incoming set's
  layout against the shape's expected layout BY NAME (names/kinds in order) - exception on mismatch. A `SharedResourceSet<T>` type would
  detect nothing extra and cannot stop the wrong texture being supplied; diminishing returns, dropped.
- **Values are not bindings.** `UniformBlock<ShadowTransforms>` makes the SLOT `LightMVP` unambiguous, not the math in it. That is OUR
  data causing OUR bugs - D0's boundary - and is the only class of visual bug left when every gate is green. What the shape forbids is
  the OTHER thing: alignment fixed "by happenstance" by rearranging fields until compiler and C# agree.
- **Slot-numbered CommandList binders: INTERNAL.** `SetGraphicsResourceSet(slot, set)`, `SetComputeResourceSet(slot, set)`,
  `SetVertexBuffer(slot, buffer)` are bypasses; renderer code binds only through `shape.BindResourceSets(cl)` / `shape.BindVertexBuffers(cl, ...)`.
  Index buffers and non-shader buffers keep their public API (nothing to name).
- Storage buffers / images: same pattern as uniforms; dynamic offsets: same names + an index. No new gap.

### 5.4 S9 - source-generated shapes: the same contract at C# COMPILE time, no runtime reflection (user, 2026-10-31) - DEFERRED; shapes are declared `partial` now so it needs no API change later

**Goal:** FluidUI (precompiled bundles, Android/AOT) gets the contract as Roslyn build ERRORS and zero reflection at runtime. 5.3's API is
designed so this is an alternative IMPLEMENTATION of the same contract, not a second contract:
1. Shapes are `partial`; the generator adds a partial. (Roslyn generators can only ADD syntax trees, never edit a declaration, so adding `Describe()` / the descriptor / the hash to the user's class REQUIRES `partial`; diagnostics need nothing. A non-partial `ShaderPipelineShape` subclass is itself a generator ERROR, so it cannot be forgotten. The alternative - a separate generated descriptor type found via an assembly attribute - works and is AOT-safe but is one more runtime lookup and one more place a shape can be missing; rejected.)
2. The verification input is `ShapeDescriptor` (data). Reflection builds it in 5.3; the generator emits it as a static readonly instance,
   overriding `Describe()`. Slot objects are constructed from descriptor + bundle, never from `FieldInfo`. `Bind` = Verify(Describe(), bundle)
   + create slots, identical for both implementations.
3. The compiled interface exists at C# compile time: the `.interface.json` snapshot (S2c, committed beside each bundle) is an
   `AdditionalFile`. Precompiled consumers have it; runtime-compiled consumers already run the shader compile before `CoreCompile`
   (RenderKit targets), so it exists in time.
The generator (`Veldrid.ShaderShapes.Generator`, shipped as an analyzer in the Veldrid package): for every `partial class X : ShaderPipelineShape`,
find its bundle's interface json (by a `[ShaderBundle("ShadowDepth")]` attribute on the class - the ONE string, and it names a file, not a field),
then (a) report a Roslyn ERROR for each mismatch - "field `AlbedoTex` has no GLSL resource in bundle ShadowDepth", "GLSL block `MaterialParams`
has no field", "`ShadowTransforms.LightMVP` is at offset 0, the shader member is at 16", "GLSL name `gl_Foo` is not a legal C# identifier" -
so the contract fails the C# BUILD; (b) emit the static `ShapeDescriptor` and vertex layouts (offsets computed for sequential blittable structs);
(c) emit `const string CompiledInterfaceHash`; `Bind` compares it with the loaded bundle's hash and throws on drift (the single residual
runtime check: the bundle must be the one the code was compiled against). A shape with no generator partial falls back to reflection -
both paths run the same `Verify`.

**Order:** 5.3 reflection first (S8), then the generator (S9) BEFORE FluidUI migrates, so Fluid adopts the generated form once. Estimate: one
day for the generator + tests. Trim/AOT note: reflection over struct fields is exactly what trimming breaks; the generator removes that dependency.

**S8 checklist (D10)** - [ ]
- [ ] AN_Veldrid: `ShaderPipelineShape` (`Describe`/`Bind`/`Verify`), `ShapeDescriptor`, `VertexInput<T>` (+`[PerInstance]`, layout generation),
      `UniformBlock<T>` (+`UpdateMember` by expression), `TextureSlot`, `SamplerSlot`, `StorageBufferSlot<T>`, `StorageImageSlot`; pipeline / set /
      vertex binding on the shape; refuse non-bundle shaders; compute abstract -> Core hook; bypass APIs internal; delete S7 record / attribute /
      registry / `ShaderUniformStructs`; ContractTests (good shape; each mismatch kind; illegal identifier; per-instance order; Verify == Bind); publish.
- [ ] AN_VeldridSpirv: delete `NormalizeResourceNames`; publish.
- [ ] S9 generator (5.4) + tests; publish.
- [ ] SilkyNvg (shapes per family; sample D3D11 + Vulkan) -> FluidUI (`StretchBlitShape`, generated; gtest/vk-gtest) [D1b gate]; publish both.
- [ ] Monsters/RenderKit/SpaceGame: one shape class per renderer; `Matrix4x4_ColumnMajor` writes -> `Update(cl, in struct)`; RenderKit tests enumerate
      shapes; legacy `[ShaderUniformBlock]` + `KitUniformStructs_MatchKitManifest` retired.
- [ ] ArcaneSiege/TileMapKit/TOMF: `ChunkInstancedShape` x3 modes, `TileLitShape`, `SpriteShape`, `MultiTextureShape`, `ParallaxShape`, debug shapes,
      `TomfBillboardShape`; ImGui renderer (raw bytecode) -> bundle + shape; all six gates 0 px.
- [ ] Spec: D0 detector list gains "a binding without a name is impossible by API"; S7 closed as subsumed.

## 6. Open

- O1. `preserve_interface` in the pinned SPIRV-Tools (`ext/known_good.json`) and header availability to `libveldrid-spirv` (decides 4.1 option 1 vs 2). First task of S2.
- O2. `flat` strictness in V5b: mismatch is an error (fxc interpolation modifiers change packing) - decided yes.
- O3. Compute: interface = blocks + resources; V7b/V7c apply; no varyings.
- O4. GLSL/ESSL varying renaming (`vdspv_fsinN`) happens after reflection capture - confirm in S1.
- O5. V7c discovery: explicit `ShaderUniformBlockRegistry.Register(typeof(X).Assembly)` vs scanning all loaded assemblies. Explicit preferred (AOT-friendly); a struct never registered is a gap - mitigated by S4's fast-lane test enumerating all `[VdShaderUniformBlock]` types.
- O6. Optional D3D11 belt-and-braces: `D3DReflect` DXBC signature comparison VS-out vs PS-in. With D3+D4 it can only agree; add only if cheap.

## 7. References

- `AN_VeldridSpirv/src/libveldrid-spirv/libveldrid-spirv.cpp` (`CompileVertexFragment` 330-530, `CompileGlslToSpirv` 704-758, `ReflectVertexInfo` 811-849); `src/Veldrid.SPIRV/{ShaderBundleCompiler,SpirvReflection,ReflectionInfo}.cs`
- `AN_Veldrid/src/Veldrid/ShaderBundle/{VeldridShaderBundle,PrecompiledShaderResult,VdShader*}.cs`; `ResourceLayout.Description`
- `AN_Monsters/_SPECS_RenderKit/60_ShaderInterfaceContract.md` (consumer side), `src/AN.RenderKit/Gpu/{PipelineCache,ShaderUniformBlockAttribute,EmbeddedShaderBundleSource}.cs`, `src/Monsters/Rendering/RuntimeGlslShaderSource.cs`, `Scripts/CompileShaders`
- `AN_ArcaneSiege/src/AN.TileMapKit/Rendering/ShaderLoader.cs`, `Assets/Shaders/ChunkInstanced*.glsl`, `src/AN.TileMapKit.Tests/ShaderCrossCompileDumpTests.cs` (temporary evidence harness; HLSL dumps in `TestResults/shader_dump/`)
