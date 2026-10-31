# Shader interface contract - typed slot ARGUMENTS, same method calls (API correction)

Status: SPEC approved 2026-10-31 (rev 2, user's literal rule); supersedes the S8 "verbs on slot objects" surface. Implement in Veldrid before continuing Silky.
Parent: [tracking](BUGFIX_ShaderInterfaceContract_IMPL_Tracking.md); NAME-verification rationale: [NameVerified](BUGFIX_ShaderInterfaceContract_NameVerified.md).

## The rule (user, verbatim intent)

> **The API method calls are EXACTLY the same. Not a single call changes. Every `uint` slot / set / binding index - including array
> POSITIONS that are slot numbers - is replaced by a C# type that carries the verified name.**

Nothing else is restructured. Verification happens at `ShaderPipelineShape.Bind` (C# fields <-> GLSL, both directions) and inside the
SAME factory/command methods, which now receive typed slots and therefore can check them. The only exception is
`GraphicsDevice.UpdateBuffer` -> `UpdateBufferOutsideCommandList`, decided separately for ordering reasons.

```csharp
class CommandList {
    public void SetVertexBuffer<TVertex>(VertexInput<TVertex> input, ShaderVertexBuffer<TVertex> buffer) where TVertex : unmanaged
    {
        uint slot = input.SlotIndex;
        SetVertexBuffer(slot, buffer.Buffer);      // SAME CODE
    }
}
```

## What S8 got wrong (reversed by this spec)

1. Verbs moved onto slot objects (`shape.Vertices.Bind(cl, vb)`, `block.Update(cl, v)`, `shape.BindResourceSets(cl)`, `shape.CreateGraphicsPipeline(...)`). REMOVED.
2. `ResourceSet` / `ResourceLayout` creation hidden inside the shape, with a shape-owned set cache and per-slot bound state. REMOVED; consumers
   create and own layouts/sets exactly as before, with typed arguments.
3. `ShapeCommandBindings` invented invalidation (vertex bindings blanked on every `SetPipeline`). Replaced by type/identity readiness.

## 1:1 mapping - every call keeps its name and arity; only the `uint`/positional argument changes type

| Call today (S7 / raw) | Call after | What the type carries |
|---|---|---|
| `new ResourceLayoutDescription(elem0, elem1, …)` | `new ResourceLayoutDescription(shape.ViewSize, shape.FontAtlas, …)` | `ShapeSlot`: name, kind, stages, set, binding (array position was the binding uint) |
| `factory.CreateResourceLayout(desc)` | unchanged | checks: all slots one shape, one set, complete, in binding order; applies the bundle's flat-binding map; stamps `layout.VerifiedShape/SetIndex` |
| `new ResourceSetDescription(layout, res0, res1, …)` | `new ResourceSetDescription(layout, shape.ViewSize.With(buf), shape.FontAtlas.With(view), …)` | `ShapeResourceBinding` from a slot's `With(...)`: resource type checked per slot kind |
| `factory.CreateResourceSet(desc)` | unchanged | checks: layout is shape-verified, bindings are that shape+set, exactly once each; stamps `rs.VerifiedShape/SetIndex/OffsetArenas` |
| `new ShaderSetDescription(vertexLayouts[], shaders[, specializations])` | `new ShaderSetDescription(new VertexInput[]{ shape.Vertices }, bundle.Shaders[, specializations])` | array position was the vertex slot uint; `VertexInput` carries layout + slot index + owner |
| `new GraphicsPipelineDescription(blend, ds, raster, topo, shaderSet, layouts[], outputs)` | **unchanged** | layouts[] position = set index; checked against each layout's verified set |
| `factory.CreateGraphicsPipeline(desc)` | unchanged (public again) | checks: shader set and every layout belong to ONE shape; shaders == bundle shaders; stamps `pipeline.VerifiedShape` |
| `new ComputePipelineDescription(shader, layouts[], x, y, z)` / `factory.CreateComputePipeline(desc)` | unchanged (public again) | same checks |
| `cl.SetVertexBuffer(uint slot, DeviceBuffer vb[, uint byteOffset])` | `cl.SetVertexBuffer(VertexInput<T> input, ShaderVertexBuffer<T> vb[, uint byteOffset])` | byte offset is not a slot; unchanged |
| `cl.SetGraphicsResourceSet(uint set, ResourceSet rs)` | `cl.SetGraphicsResourceSet(ResourceSet rs)` | the set index is carried by `rs` (via its layout, via its slots) - the uint was a redundant copy |
| `cl.SetGraphicsResourceSet(uint set, rs, uint n, ref uint offsets)` / `(…, uint[] offsets)` | `cl.SetGraphicsResourceSet(ResourceSet rs, params UniformArenaElement[] elements)` | byte offsets -> typed arena elements, in the set's offset-selectable binding order |
| `cl.SetComputeResourceSet(...)` | same transformation | |
| `cl.UpdateBuffer(DeviceBuffer, uint byteOffset, T)` | unchanged for raw `DeviceBuffer` | |
| (typed storage) | `cl.UpdateBuffer(UniformBlock<T>, in T)`, `cl.UpdateBuffer(UniformBlock<T>, b => b.Field, in TMember)`, `cl.UpdateBuffer(ShaderUniformBuffer<T>, in T)`, `cl.UpdateBuffer(ShaderVertexBuffer<T>, uint firstVertex, ReadOnlySpan<T>)`, `cl.UpdateBuffer(ShaderUniformArena<T>)` | each = one raw `UpdateBuffer`; the byte offset of a whole-block write is gone because the typed storage IS the block |
| `factory.CreateBuffer(BufferDescription)` | unchanged for raw buffers; typed storage via `shape.X.CreateBuffer(usage)` / `CreateSharedBuffer(usage)` / `CreateArena(device, capacity, usage)` / `shape.Vertices.CreateBuffer(capacity, usage)` | creation stays on the verified slot: that is where the type is checked |

Raw-positional constructors (`ResourceLayoutDescription(ResourceLayoutElementDescription[])`, `ResourceSetDescription(layout, IBindableResource[])`,
`ShaderSetDescription(VertexLayoutDescription[], …)`) become `internal`: the typed constructors build them.

### Slot binding values

| Slot | `With(...)` accepts | Checked |
|---|---|---|
| `UniformBlock<T>` | `With()` = owned buffer from `CreateBuffer`; `With(ShaderUniformBuffer<T>)`; `With(ShaderUniformArena<T>)` (requires `[UniformBufferBindingTimeOffsetSelectable]`, binds a one-block range) | same `T`, same factory, size == compiled block, liveness |
| `TextureSlot` | `TextureView`, `Texture` | kind, liveness |
| `SamplerSlot` | `Sampler` | kind, liveness |
| `StorageBufferSlot` / `StorageImageSlot` | as today | kind |

## Command-recording contract (mirrors the GPU; no invented invalidation)

- `SetPipeline(p)`: owner = `p.VerifiedShape`. A vertex slot stays READY iff the buffer currently bound there has the same `TVertex` (and
  factory) as the new owner's `VertexInput` at that slot. A resource set stays READY iff the bound `rs.VerifiedShape` is the new owner (same
  instance => same layouts). Otherwise not ready; draw throws the existing `ShaderContractException`.
- `SetVertexBuffer(input, vb)`: `input.Owner == owner`. `SetGraphicsResourceSet(rs, …)`: `rs.VerifiedShape == owner`; element count ==
  offset-selectable binding count; offsets computed from the set's arenas.
- Draw/Dispatch: every vertex input and every set of the owner ready. Unchanged otherwise.

## Veldrid changes

- [x] `ShaderPipelineShape.cs`: shapes are verified descriptors + storage factories. No bound state, no set cache, no verbs, no pipeline creation.
      Slots gain `With(...)`; `UniformBlock` keeps `CreateBuffer/CreateSharedBuffer/CreateArena` and gains `ResolveMemberOffset` (for `cl.UpdateBuffer(block, expr, v)`).
- [ ] `ResourceLayoutDescription(params ShapeSlot[])`; raw ctor internal. `ResourceLayout.VerifiedShape/VerifiedSetIndex`.
- [ ] `ResourceSetDescription(ResourceLayout, params ShapeResourceBinding[])`; raw ctor internal. `ResourceSet.VerifiedShape/VerifiedSetIndex/VerifiedOffsetArenas`.
- [ ] `VertexInput` non-generic base; `ShaderSetDescription(VertexInput[], Shader[][, SpecializationConstant[]])`; raw ctors internal; `VerifiedShape`.
- [ ] `ResourceFactory`: `CreateResourceLayout`, `CreateResourceSet`, `CreateGraphicsPipeline`, `CreateComputePipeline` public with the checks above.
- [ ] `CommandList`: typed `SetVertexBuffer`, `SetGraphicsResourceSet`, `SetComputeResourceSet`, `UpdateBuffer` forms (forwarding). `uint` forms stay internal.
- [ ] `ShapeCommandBindings`: type-based vertex readiness; shape-identity set readiness.
- [ ] Storage types lose their verbs (done): `ShaderUniformBuffer.Update`, `ShaderUniformArena.Upload`, `ShaderVertexBuffer.Update`.
- [ ] ImGui: same-name forms; explicit per-texture `ResourceSet` cache as its pre-S8 code had.
- [ ] ContractTests rewritten to the same-name forms; new: same-`TVertex` pipeline switch needs no rebind / different-`TVertex` throws; set from another
      shape rejected; wrong/missing/duplicate slot in layout or set rejected; arena elements land (readback). StableABI gate: every S7 public
      `CommandList`/`ResourceFactory`/description API exists with the same name, only `uint`/positional -> typed.
- [ ] Build, 100% ContractTests, commit, publish, record version.

## Silky transformation (mechanical, after the Veldrid publish)

| Silky today | Silky after |
|---|---|
| `commandList.SetVertexBuffer(0, _vertexBuffer)` | `commandList.SetVertexBuffer(shape.Vertices, _vertexBuffer)` (`ShaderVertexBuffer<NvgVertex>`; one bind per flush as today) |
| `commandList.SetGraphicsResourceSet(0, set, 1, ref paintOffset)` | `commandList.SetGraphicsResourceSet(set, paintElement)` |
| `factory.CreateResourceLayout(WithDynamicPaintBinding(bundle.ResourceLayouts[0], …))` | `factory.CreateResourceLayout(new ResourceLayoutDescription(shape.ViewSize, shape.PaintParams, …))` (offset-selectable comes from the field attribute) |
| `factory.CreateResourceSet(new ResourceSetDescription(layout, _viewSizeUniformBuffer, view, sampler, _textGammaUniformBuffer, paintRange))` | `factory.CreateResourceSet(new ResourceSetDescription(layout, shape.ViewSize.With(_viewSizeUniformBuffer), shape.FontAtlas.With(view), shape.FontAtlasSampler.With(sampler), shape.TextGammaParams.With(_textGammaUniformBuffer), shape.PaintParams.With(_paintArena.Storage)))` |
| `commandList.UpdateBuffer(_viewSizeUniformBuffer, 0, v)` | `commandList.UpdateBuffer(_viewSizeUniformBuffer, v)` |
| `new GraphicsPipelineDescription(…, new ShaderSetDescription(new[]{vertexLayout}, shaders), layouts, outputs)` | `new GraphicsPipelineDescription(…, new ShaderSetDescription(new VertexInput[]{ shape.Vertices }, shaders), layouts, outputs)` |

All of Silky's resource-set caches, eviction hooks, transient-set lifetimes, paint-arena residency/offset logic, upload counts and buffer
allocations stay exactly as they are. `PaintUniformArena.DynamicOffsetOf` returns a `UniformArenaElement` instead of a byte offset.