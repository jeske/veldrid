# Typed buffer policy audit — initial matrix and decision proposal

Status: initial source audit plus explicit-allocation implementation slice. Not a complete synchronization audit or all-mechanisms implementation.
Implementation tracker: [Typed buffer policies](BUGFIX_TypedBufferAccessPolicies_IMPL.md).

## Evidence and provenance
Native documentation snapshots are in `_BUGFIX/BufferPolicies_NativeDocs/` (fetched 2026-10-31; they first landed in AN_ArcaneSiege/_EXTERNAL_APIS because url_fetch could only write to the active workspace - moved here 2026-11-01).
- Microsoft UpdateSubresource: https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource
  - Constant buffers cannot be partially updated through this method.
  - Dynamic and immutable destinations are prohibited.
  - Source bytes are snapped/copied before return; contention may require temporary command storage plus a GPU copy.
- Apple archived Resource Options (2017): https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/ResourceOptions.html
  - Private storage is GPU-only; Shared is CPU/GPU accessible; Managed requires explicit synchronization.
  - CPU writes to Managed buffers require didModifyRange; GPU writes require synchronizeResource and completion before CPU read.
  - Historical discrete-Mac recommendations are NOT universal recommendations for current Apple Silicon.
- Vulkan vkMapMemory: https://docs.vulkan.org/refpages/latest/refpages/source/vkMapMemory.html
  - HOST_VISIBLE required; map does not wait for device use. Host writes must wait for prior overlapping device reads/writes; host reads must wait for writes.
  - HOST_COHERENT does not remove execution/lifetime synchronization obligations.
- Vulkan_CopyBuffer.md is a bot-challenge response, NOT API evidence. Copy-buffer native documentation still needed.

## Observed implementation: allocation
| Existing declaration | D3D11 | Vulkan | Metal | OpenGL |
|---|---|---|---|---|
| Shader role without Dynamic/Staging | Default usage, no CPU access flags | DeviceLocal requested | Private | StaticDraw allocation hint |
| Shader role + Dynamic | Dynamic usage, CPU Write | HostVisible + HostCoherent requested | Shared | DynamicDraw allocation hint |
| Staging | Staging usage, CPU Read + Write | HostVisible + HostCoherent, HostCached when available | Shared | No separate storage class in allocator |

Code: D3D11/D3D11Buffer.cs, Vk/VkBuffer.cs, MTL/MTLBuffer.cs, OpenGL/OpenGLBuffer.cs.
Vulkan DeviceLocal is a requested property, not a guarantee that the selected memory type lacks HostVisible.
Metal buffer allocator currently has no Managed selection. Do not expose a Managed policy claiming support before implementing synchronization.

## Observed implementation: command-recorded uploads
| Backend | Current path | Important restriction / audit boundary |
|---|---|---|
| D3D11 non-Dynamic full uniform | UpdateSubresource, null destination box | Whole uniform allocation must be provided |
| D3D11 Dynamic full allocation | Map/WriteDiscard and copy | NOT the working Silky view-update path; preserve Silky's choice |
| D3D11 remaining updates | Temporary staging then CopyBuffer | Not automatically valid for every destination/range; partial constant-buffer copying needs native legality review |
| Vulkan | CPU writes staging; records vkCmdCopyBuffer; post-copy visibility barrier | Read-before-overwrite ordering and broader GPU-write/indirect uses still require full dependency audit |
| Metal | CPU writes staging; encodes blit (unaligned cases use compute copy) | Encoder transitions and staging retirement must be audited; no Metal runtime result yet |
| OpenGL | Snapshot source in recorded entry; executor calls gl(Named)BufferSubData | Driver-managed update mechanism, not an excuse to skip portable declaration validation |

Code: backend CommandList.UpdateBufferCore; OpenGLNoAllocCommandEntryList.UpdateBuffer and OpenGLCommandExecutor.UpdateBuffer.

## Observed API validation gaps
- FIXED first slice: shape binding no longer allocates uniform buffers; CreateBuffer requires explicit usage.
- FIXED first slice: ShaderVertexBuffer creation requires explicit usage instead of forcing Dynamic.
- ShaderUniformArena creation now also requires explicit usage; existing callers choose non-Dynamic full-buffer uploads.
- GraphicsDevice.Map declaration checks are under VALIDATE_USAGE; required typed policy enforcement must also run in Release.
- UpdateMember now rejects partial constant-buffer writes through the current D3D11 path; broader operation coverage is still pending.
- Current typed storage slots reject storage buffers (reflection coverage incomplete); a general buffer-policy design must not silently reopen unchecked SSBO binding.

## Proposed policy structure (not approved names)
Use separate declarations for:
1. GPU binding role and verified data layout (existing named typed slots).
2. Binding-time offset selection (approved attribute name).
3. Host access / storage strategy and permitted update operations.
4. Ownership of in-flight resource versions and synchronization.

Do not treat a raw BufferUsage bitmask as a complete policy: it lacks ordering and ownership semantics.
Initial concrete implementation should cover device-local/default/private, command-recorded full uniform uploads, preserving Silky.
Additional mechanisms to inventory and support deliberately: initialization-only resources; CPU-write mapped streaming; upload staging; readback staging; explicit GPU copies; GPU-written storage/indirect buffers; Metal Managed coherence where supported. Capability-specific mechanisms must have explicit refusal on unsupported platforms, not a universal support claim.

## Resolved decisions and first implementation slice
### D1 — Require explicit policy
APPROVED with correction: specify choices at their native lifecycle points. Storage/CPU access at resource creation; offset-selection capability at layout creation; resource/range at binding; operation and range at update/map/copy. No mandatory storage-policy attributes on shader fields.
First slice: Bind creates verified handles only. UniformBlock.CreateBuffer(BufferUsage) explicitly allocates owned storage; VertexInput.CreateBuffer and UniformBlock.CreateArena require usage arguments. These preserve existing backend flags and do not pretend those flags are the complete future access contract.

### D2 — Named member changes on full-upload uniform storage
Mandatory staging/shadow proposal REJECTED. Preserve direct supported operations and their costs. Whole-value Update issues one existing backend update, without new staging or CPU shadow at the typed layer.
Member writes remain direct where the existing backend supports them. Current D3D11 partial constant-buffer UpdateMember throws before recording: its existing route does not provide the required partial operation. No hidden widening. This is NOT a claim that every native API or newer D3D11 operation forbids partial constant-buffer updates.
Explicit optional arenas keep their existing staging/batching helper. Broader direct transfer/mapping/storage support remains pending.

## Remaining audit before broader support
- Complete offset-binding alignment/range/capability matrix using native documentation.
- Immediate versus recorded updates across all backends.
- Readback completion and typed mapped lifetime enforcement.
- Metal Managed support versus declared unsupported capability, and available macOS validation environment.
- GPU write / transfer / indirect barriers and resource lifetime across command lists.
- Managed-versus-native layout checks remain the parent shader-contract audit's separate obligation.
