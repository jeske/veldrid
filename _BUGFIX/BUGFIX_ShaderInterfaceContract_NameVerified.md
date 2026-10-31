# BUGFIX: Shader interface contract - every boundary verified BY NAME, declared interface never eliminated

**Status:** SPEC 2026-10-31, implementation starting (phases S1..S5 below).  
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

**S1 - AN_VeldridSpirv: reflect + SCREAM (V5b, V7-ready interface) + the one pipeline (4.5)** - [ ]
- [ ] native 4.2 reflection + ABI bump; C# 4.3 types; `SpirvReflection.Interface`.
- [ ] `ShaderInterfaceVerifier.VerifyStages(interface)` (V5b) called inside `ShaderBundleCompiler.CompileVertexFragment`; throws `ShaderInterfaceContractException`.
- [ ] `ShaderBundlePipeline` (4.5): `BuildBundle` (compile+verify+serialize) and `GetOrBuild` (content-hash cache, both build-time and runtime); `ShaderBundleRuntimeSource` over it.
- [ ] `Veldrid.SPIRV.Tests`: ArcaneSiege `ChunkInstanced` vert + vertex-lit frag as fixture -> throws naming `fragTileUV`, `fragTileRowParity`, `fragGridTile`; matched pair passes; same names at swapped locations throws; flat mismatch throws.
- [ ] GATE: the fixture throws. **We know.** AND SilkyNvg's + FluidUI's existing GLSL compiled through the new compiler: every pair must pass V5b
      or the failure must be a REAL latent mismatch we then fix in the GLSL (record each in 6. as evidence).

**S2 - AN_VeldridSpirv: D3 preserve interface; V4 / V5a / V6 declared-vs-compiled** - [ ]
- [ ] O1 answered; 4.1 implemented; test: debug == optimised `FragmentInputs` for a frag with an unread input.
- [ ] GLSL declaration parser (per stage: `layout(location = N) [flat] in|out TYPE name;`, `uniform Block { members };` after `#define`-free preprocessing) -> V4/V5a/V6 in the compiler.
- [ ] GATE: the ArcaneSiege frag with 3..5 declared-unused now compiles with V5b passing; HLSL dump shows TEXCOORD3..5 in the PS struct.
- [ ] publish-local AN_VeldridSpirv (interim, against the current Veldrid) so G-Fluid can run early on the compile-side checks.

**S3 - AN_Veldrid: bundle interface + V7 at pipeline creation** - [ ]
- [ ] `VeldridShaderBundle.Interface` + JSON context + format version bump (old bundles rejected with a clear message); `PrecompiledShaderResult.Interface`.
- [ ] `ShaderInterfaceRegistry`, `ShaderContractVerifier`, `VdShaderUniformBlockAttribute`, `ShaderUniformBlockRegistry`; hooks in `ResourceFactory.CreateGraphicsPipeline` / `CreateComputePipeline`.
- [ ] tests: synthetic descriptions - renamed vertex element, swapped resource names at same slots, reordered struct field, wrong struct size -> each throws with the right rule id.
- [ ] publish-local AN_Veldrid; bump AN_VeldridSpirv's Veldrid dependency; publish-local AN_VeldridSpirv.

**S4 - AN_SilkyNvg + AN_FluidUI: the primary consumer (G-Fluid)** - [ ]
- [ ] SilkyNvg: Build.props bump; `Scripts/CompileShaders_Program` -> thin call into `ShaderBundlePipeline.GetOrBuild` (hash manifest semantics preserved);
      recompile all bundles (format bump); fix any GLSL that V4-V6 rejects; sample renders D3D11 + Vulkan; publish-local.
- [ ] FluidUI: Build.props bumps (Veldrid, SPIRV, SilkyNvg); `FluidUI.GameCompositor/Scripts/CompileShaders_Program` -> thin; `StretchBlit.vdshader`
      recompiled; `StretchBlitRenderer` unchanged in shape (`CreateFromBundle` now registers the interface, V7 runs at its pipeline creation);
      `cmd\gtest.bat`, `cmd\vk-gtest.bat`, verifyall as the repo's own gates dictate; publish-local.
- [ ] Android: bundles are consumed with no native compiler - confirm the loader path has no new native dependency (interface is JSON in the bundle).

**S5 - AN_Monsters: consumers adopt (spec 60 keeps this list)** - [ ]
- [ ] `Monsters.Build.props ANVeldridSPIRVVersion` bump; `Scripts/CompileShaders` inherits V4-V6; fix every failing bundle (the `_unused`/dead-reference hacks become plain declarations).
- [ ] `Scripts/CompileShaders` and `EmbeddedShaderBundleSource` -> thin over 4.5; Monsters `RuntimeGlslShaderSource` DELETED in favour of `ShaderBundleRuntimeSource`
      (an `IShaderSource` adapter of a few lines stays in RenderKit); `[ShaderUniformBlock]` -> `[VdShaderUniformBlock]`; `ShaderBundleContractAssert`
      V3 test kept as the fast lane; `ShaderUniformBlockRegistry.Register` at startup.
- [ ] GATES: G-Monsters.

**S6 - AN_ArcaneSiege / TileMapKit: the runtime path joins** - [ ]
- [ ] `ArcaneSiege.Build.props` bumps; TileMapKit `ShaderLoader` -> thin adapter over `ShaderBundleRuntimeSource` (cache under `%LOCALAPPDATA%/ArtificialNecessity/ShaderCache/<app>`);
      `CreateFromSpirv` gone (grep gate across all repos), then `[Obsolete(error: true)]` in AN_Veldrid.SPIRV.
- [ ] `ChunkInstanced.frag` / `_Unlit.frag` keep the full declarations (now the rule, comments updated to cite this spec); delete `ShaderCrossCompileDumpTests`; `_SPECS_TOMF/10` Phase 3b closed by D1.
- [ ] GATES: G-Arcane; negative test: a frag with a missing varying fails at LAUNCH with the table.

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