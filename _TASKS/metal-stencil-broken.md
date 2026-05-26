# TASK: Metal Backend Stencil Operations Not Working

## Problem

Stencil-then-cover rendering (non-convex path fill) does not work on the Metal backend. Convex fills and direct draws work correctly. The same Veldrid pipeline states work on D3D11, Vulkan, OpenGL, and OpenGL ES.

## Evidence

SilkyNvg demo on macOS Metal:
- ✅ Convex gradient fills (triangle in color wheel) — works
- ✅ Text rendering — works
- ✅ Image rendering — works
- ✅ Solid color fills — works
- ❌ Color wheel ring (non-convex, stencil fill) — no gradient visible
- ❌ Background gradient (non-convex fill) — missing
- ❌ Eye white ovals (gradient on stencil path) — missing

The same code renders correctly on:
- Windows D3D11 ✓
- Windows Vulkan ✓
- Android Vulkan ✓
- Android OpenGL ES ✓
- macOS OpenGL (via Veldrid OpenGL backend) ✓

## Stencil Pipeline States Used

### Stencil Fill Pass (write stencil, no color output):
```csharp
DepthTestEnabled = false,
DepthWriteEnabled = false,
StencilTestEnabled = true,
StencilFront = (IncrementAndWrap, IncrementAndWrap, IncrementAndWrap, Always),
StencilBack = (DecrementAndWrap, DecrementAndWrap, DecrementAndWrap, Always),
StencilReadMask = 0xFF,
StencilWriteMask = 0xFF,
StencilReference = 0
ColorWriteMask = None  // no color output, stencil only
```

### Stencil Cover Pass (draw where stencil != 0, zero stencil):
```csharp
DepthTestEnabled = false,
DepthWriteEnabled = false,
StencilTestEnabled = true,
StencilFront = (Zero, Zero, Zero, NotEqual),
StencilBack = (Zero, Zero, Zero, NotEqual),
StencilReadMask = 0xFF,
StencilWriteMask = 0xFF,
StencilReference = 0
```

## Possible Causes in Veldrid Metal Backend

1. **Stencil state not being applied to the Metal render pipeline** — the `MTLDepthStencilState` might not be created correctly from the Veldrid `DepthStencilStateDescription`
2. **Stencil reference value not being set** — Metal requires `setStencilReferenceValue:` on the render command encoder
3. **D32FloatS8UInt stencil component not working** — the 8-bit stencil in this combined format might not be properly addressed on Metal
4. **Front/back face stencil operations swapped** — Metal's face winding convention might differ from what Veldrid expects

## Where to Look

- `src/Veldrid/MTL/MTLCommandList.cs` — where stencil state is applied to the render encoder
- `src/Veldrid/MTL/MTLPipeline.cs` — where `MTLDepthStencilState` is created from the pipeline description
- Search for `setStencilReferenceValue`, `MTLStencilDescriptor`, `stencilAttachment`

## Verification

After fix: the SilkyNvg demo's color wheel ring should show a rainbow gradient, and the background should have a gradient. Run `./cmd/mac-veldrid.sh` in the AN_SilkyNvg repo.
