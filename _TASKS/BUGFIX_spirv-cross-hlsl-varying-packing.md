# BUGFIX: SPIRV-Cross HLSL Backend Packs Varyings Into Shared Registers — Breaks D3D11

## Status: CONFIRMED BUG — Workaround Applied, Proper Fix Needed

## Summary

SPIRV-Cross's HLSL backend packs multiple shader varyings (inter-stage variables) into
shared output/input registers (e.g., `float3 Normal` + `float ViewDist` packed into a single
`float4 o0`). This is an optimization to reduce interpolator slot usage, but it breaks on D3D11
when the vertex shader and fragment shader have **different varying sets** (i.e., the fragment
shader doesn't use all outputs from the vertex shader).

## Symptoms

- **Terrain renders correct geometry** (vertex shader heightmap sampling works perfectly)
- **Terrain has no textures/lighting** — fragment shader reads constant UV values for all pixels
- **All pixels sample the same texture location** (flat color across entire terrain)
- **Works on Vulkan**, broken on D3D11
- **Works on D3D11 with the workaround** (declaring unused varyings in fragment shader)

## Root Cause (Verified via RenderDoc)

### The Vertex Shader Output (compiled HLSL, from SPIRV-Cross):

```hlsl
struct SPIRV_Cross_Output
{
    float3 frag_Normal : TEXCOORD0;              // VS outputs this
    nointerpolation int frag_InstanceIndex : TEXCOORD1;  // VS outputs this
    float2 frag_HeightmapUV : TEXCOORD2;         // VS outputs this
    float2 frag_WorldXZ : TEXCOORD3;             // VS outputs this
    float frag_ViewDist : TEXCOORD4;             // VS outputs this
    float4 gl_Position : SV_Position;
};
```

### The Fragment Shader Input (compiled HLSL, from SPIRV-Cross):

```hlsl
struct SPIRV_Cross_Input
{
    float3 frag_Normal : TEXCOORD0;              // PS reads this
    // TEXCOORD1 (frag_InstanceIndex) NOT declared — PS doesn't use it
    float2 frag_HeightmapUV : TEXCOORD2;         // PS reads this
    float2 frag_WorldXZ : TEXCOORD3;             // PS reads this
    float frag_ViewDist : TEXCOORD4;             // PS reads this
};
```

### The D3D11 HLSL Compiler Packing (observed in RenderDoc):

**Vertex shader output registers:**
```
o0.xyz = frag_Normal (TEXCOORD0)
o0.w   = frag_ViewDist (TEXCOORD4)  ← PACKED into same register as Normal!
o1.x   = frag_InstanceIndex (TEXCOORD1)
o2.xy  = frag_HeightmapUV (TEXCOORD2)
o2.zw  = frag_WorldXZ (TEXCOORD3)
```

**Fragment shader input registers:**
```
v0.xyz = TEXCOORD0 (frag_Normal)
v0.w   = TEXCOORD4 (frag_ViewDist)
v1.xy  = TEXCOORD2 (frag_HeightmapUV)
v1.zw  = TEXCOORD3 (frag_WorldXZ)
```

### The Problem:

D3D11 matches VS outputs to PS inputs by **semantic name** (TEXCOORD0, TEXCOORD2, etc.).
When SPIRV-Cross packs `frag_Normal` (TEXCOORD0, 3 components) and `frag_ViewDist`
(TEXCOORD4, 1 component) into the same `float4` output register, and the PS also expects
them packed the same way, it SHOULD work — both compilers agree on the packing.

But when the PS **omits** a varying that the VS declares (like `frag_InstanceIndex` at TEXCOORD1),
the D3D11 HLSL compiler may pack the remaining PS inputs differently than the VS packed its
outputs, because the packing algorithm sees a different set of semantics. This causes
the interpolators to be mismatched — the PS reads the wrong data from the wrong slots.

**Observed result:** `v1.zw` (frag_WorldXZ) reads as `(0.0, 1.0)` for ALL pixels —
which is the Y,Z components of `frag_Normal` (which is set to `(0, 1, 0)` = straight up).
The UV coordinates are garbled, so all texture samples read the same texel.

## Why Vulkan Doesn't Have This Problem

Vulkan uses **explicit `location` numbers** for varyings. Each `layout(location = N)` in the
vertex output maps directly to `layout(location = N)` in the fragment input. No semantic
matching, no packing heuristics. If the fragment shader doesn't declare location 1, it simply
doesn't read it — locations 2, 3, 4 still map correctly.

## Current Workaround (Applied in AN_Monsters)

Add the unused varying to the fragment shader GLSL source so both VS and PS have identical
varying declarations:

```glsl
// In fragment shader — declare even though unused, to prevent D3D11 packing mismatch
layout(location = 1) in flat int frag_InstanceIndex;
```

Then add a dead-code reference to prevent the GLSL compiler from optimizing it out:
```glsl
if (frag_InstanceIndex < -999999) discard;
```

This forces SPIRV-Cross to emit TEXCOORD1 in both the VS output struct and the PS input
struct, making the packing identical.

## Proper Fix (TODO)

The fix belongs in SPIRV-Cross's HLSL backend (`spirv_hlsl.cpp`) in the AN_VeldridSpirv repo.

### Option A: Disable cross-semantic packing for stage I/O
Force each SPIR-V location to get its own HLSL register — never pack multiple TEXCOORDs
into a single `float4`. This wastes interpolator slots but guarantees correct matching.
The HLSL output struct should emit:
```hlsl
float3 frag_Normal : TEXCOORD0;      // own register
int frag_InstanceIndex : TEXCOORD1;  // own register
float2 frag_HeightmapUV : TEXCOORD2; // own register
float2 frag_WorldXZ : TEXCOORD3;     // own register
float frag_ViewDist : TEXCOORD4;     // own register
```
Not:
```hlsl
float4 stage_output0 : TEXCOORD0;  // Normal.xyz + ViewDist packed!
```

### Option B: Ensure PS declares all VS outputs
When generating the PS input struct, also emit declarations for VS outputs that the PS
doesn't use. This maintains packing compatibility. The PS can declare them and simply
never read them.

### Option C: Use SV_Target-style explicit packing
Add a SPIRV-Cross HLSL option (similar to `flatten_matrix_vertex_input_semantics`) that
forces 1:1 location-to-semantic mapping without component packing.

## Location in Code

| File | What |
|------|------|
| `C:\PROJECTS\AN_VeldridSpirv\ext\SPIRV-Cross\spirv_hlsl.cpp` | HLSL backend — struct emission + packing logic |
| `C:\PROJECTS\AN_VeldridSpirv\ext\SPIRV-Cross\spirv_hlsl.hpp` | Options struct (no relevant option exists yet) |
| `C:\PROJECTS\AN_VeldridSpirv\src\libveldrid-spirv\libveldrid-spirv.cpp:204-208` | Where options are set before compilation |

## SPIRV-Cross Version Info

- **Current pin:** `f51773b8` (August 8, 2024, tag `vulkan-sdk-1.3.290.0-21`)
- **Upstream commit of interest:** `0ac65a35` (January 7, 2026) — "HLSL: Fix a bunch of lingering issues with HLSL packlayouts"
- **Submodule update to head-of-tree did NOT fix this issue** (tested May 27, 2026)
- This packing behavior appears to be intentional optimization in SPIRV-Cross, not a recognized bug

## Verification

After fix:
1. Remove the `frag_InstanceIndex` workaround from `GBufferHeightmap.frag.glsl`
2. Recompile shader bundles
3. Run AN_Monsters on D3D11 — terrain should show full PBR textures
4. Verify all other shaders with mixed VS/PS varyings also work

## Context

- Discovered 2026-05-27 while debugging D3D11 texture issues in AN_Monsters
- Initially appeared to be a Veldrid D3D11 binding bug (investigated binding slots,
  resource layout creation, SRV caching, deferred context issues — all ruled out)
- RenderDoc capture proved bindings were correct and textures contained valid data
- Pixel shader debugger showed correct texture sample results but constant UV inputs
- Final diagnosis: VS/PS interpolator mismatch due to SPIRV-Cross HLSL packing