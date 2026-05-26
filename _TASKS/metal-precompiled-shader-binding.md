# TASK: Metal Backend Must Respect Precompiled Shader Buffer Indices

## Problem

Precompiled Metal shaders (from `.vdshader` bundles) render incorrectly because Veldrid's Metal backend ignores the shader's declared `[[buffer(N)]]` indices and imposes its own sequential numbering scheme.

**Observed:** SilkyNvg renders correctly on OpenGL (macOS) but produces wrong coordinates/scale on Metal (macOS). Same shader source, same ResourceLayout, same uniform data — only the backend differs.

**Root cause:** The Metal vertex shader declares `constant ViewSize& ViewSize_1 [[buffer(0)]]`, but Veldrid's Metal backend binds the vertex buffer to Metal buffer slot 0 and pushes uniform buffers to slot 1+. The shader reads vertex buffer data as if it were the `viewSize` uniform → garbage projection matrix → wrong rendering.

## Where It Breaks

1. **`MTLPipeline.cs:106`** — assigns vertex buffer to `buffer(0)` (or based on `ResourceBindingModel`)
2. **`MTLCommandList.cs:866`** — binds vertex buffer with `setVertexBuffer(mtlBuffer, offset, index)` where `index` is the pipeline-assigned slot
3. **`MTLResourceLayout.cs:40-84`** — assigns sequential buffer indices to uniform buffers starting from 0, not accounting for vertex buffer occupation

When `CreateFromSpirv()` is used at runtime, Veldrid tells SPIRV-Cross to offset buffer indices to avoid the vertex buffer slot. But `CreateFromBundle()` just passes the precompiled MSL to `CreateShader()` without any adjustment — the MSL has `[[buffer(0)]]` for the uniform, which conflicts with the vertex buffer.

## The `.vdshader` Bundle Already Has the Answer

The `.vdshader` bundle contains a `flatBindingMap` that records exactly which Metal buffer/texture/sampler slot each resource occupies in the compiled shader:

```json
"flatBindingMap": [
  { "set": 0, "binding": 0, "kind": "UniformBuffer", "stages": "Vertex", "flatIndex": 0 }
]
```

This map is the compiler's declaration of where resources live. The runtime should obey it.

## Required Fix

**Principle: The compiled shader is the source of truth. The runtime binds resources to the slots the shader declares.**

### Change 1: Vertex buffer gets a non-conflicting slot

When a pipeline uses precompiled shaders (loaded via `CreateFromBundle()`), the vertex buffer must be assigned to a Metal buffer slot that does NOT conflict with any `[[buffer(N)]]` declared in the shader.

Options:
- Use the next available slot after the highest buffer index in the `flatBindingMap`
- Use a fixed high slot (e.g., `buffer(30)`) that shaders never use
- Read the MSL source to determine which buffer indices are occupied

The simplest: assign vertex buffers to `buffer(30)` (Metal supports up to 31 buffer slots). Uniform buffers stay at whatever index the shader compiler assigned.

### Change 2: Uniform buffers bind to their declared slots

When binding a `ResourceSet` at draw time (`MTLCommandList`), use the `flatBindingMap` from the bundle to determine which Metal buffer slot each uniform buffer should be bound to — instead of computing sequential indices from the `ResourceLayout` element order.

### Change 3: `CreateFromBundle()` stores the binding map

`CreateFromBundle()` currently returns `PrecompiledShaderResult` with just shaders + ResourceLayoutDescriptions. It should also store the `flatBindingMap` so the Metal backend can access it at draw time.

## Why This Is The Right Fix

- **No more one-off hacks.** Whatever SPIRV-Cross produces is what the runtime uses. Period.
- **Works for all shaders.** Simple shaders (1 uniform) and complex shaders (multiple sets, mixed textures/samplers/uniforms) all work the same way.
- **Bundle is self-describing.** The `.vdshader` file contains everything needed to correctly bind resources on every backend. No implicit contracts between the compiler and runtime.
- **Fail-loud.** If a shader declares `[[buffer(5)]]` but the runtime tries to bind to slot 0, that's a detectable mismatch. Add validation.

## Files Changed (IMPLEMENTED 2026-05-26)

| File | Change |
|------|--------|
| `src/Veldrid/ShaderBundle/PrecompiledShaderResult.cs` | Added `FlatBindingMap` property + `CreateResourceLayouts(factory)` convenience method |
| `src/Veldrid/ResourceFactory.cs` | Pass `flatBindingMap` through in `CreateFromBundle()`; added `CreateResourceLayout(desc, setIndex, bindingEntries)` virtual overload |
| `src/Veldrid/MTL/MTLResourceLayout.cs` | Added compiler-aligned constructor that uses `flatIndex` from binding map as slot; added `HasExplicitBindingSlots` flag |
| `src/Veldrid/MTL/MTLResourceFactory.cs` | Override `CreateResourceLayout` with binding entries to use the new `MtlResourceLayout` constructor |
| `src/Veldrid/MTL/MTLPipeline.cs` | Added `VertexBufferBaseSlot` (slot 30 for explicit bindings), `HasExplicitBindingSlots` flag; vertex descriptor uses `VertexBufferBaseSlot` |
| `src/Veldrid/MTL/MTLCommandList.cs` | `getBufferBase`/`getTextureBase`/`getSamplerBase` return 0 for explicit bindings; vertex buffers use `VertexBufferBaseSlot`; `bindBuffer` skips `vertexBufferCount` offset for explicit bindings |

## How It Works

1. `CreateFromBundle()` passes `bundle.FlatBindingMap` → `PrecompiledShaderResult.FlatBindingMap`
2. User calls `result.CreateResourceLayouts(factory)` which calls `factory.CreateResourceLayout(desc, setIndex, entries)`
3. Metal factory creates `MtlResourceLayout` with the compiler-aligned constructor → `HasExplicitBindingSlots = true`, slots from `flatIndex`
4. Pipeline detects `HasExplicitBindingSlots` → vertex buffers go to slot 30, not conflicting with shader-declared buffer indices
5. At draw time, `getBufferBase()` returns 0 (slots are absolute), uniform buffers bind to their declared `[[buffer(N)]]` slots

## Client Usage

```csharp
var bundle = VeldridShaderBundle.Deserialize(vdshaderJson);
var result = factory.CreateFromBundle(bundle, fileResolver: LoadShaderFile);
Shader[] shaders = result.Shaders;
ResourceLayout[] layouts = result.CreateResourceLayouts(factory); // ← uses binding map
// Create pipeline with these layouts — guaranteed correct on Metal
```

## Verification

After the fix:
1. `./cmd/mac-veldrid.sh` (Metal) should render identically to `./cmd/mac-veldrid.sh --opengl`
2. Windows D3D11 should continue working (D3D11 has its own flat register scheme but the same principle applies)
3. Android Vulkan should continue working (Vulkan uses SPIR-V natively, no remapping)

## Context

- Discovered while getting SilkyNvg's Veldrid Example running on macOS Metal
- OpenGL backend renders correctly on the same machine (same shader logic, same uniform data)
- The `.vdshader` bundle format was implemented to solve this class of problem — it just needs the Metal backend to actually consume the binding map
