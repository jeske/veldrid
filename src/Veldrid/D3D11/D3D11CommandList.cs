using System;
using System.Collections.Generic;
using System.Diagnostics;
using SharpGen.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Veldrid.D3D11
{
    internal class D3D11CommandList : CommandList
    {
        public ID3D11CommandList DeviceCommandList => commandList;

        public override bool IsDisposed => disposed;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                DeviceContext.DebugName = value;
            }
        }

        internal ID3D11DeviceContext DeviceContext { get; }

        private readonly D3D11GraphicsDevice gd;
        private readonly ID3D11DeviceContext1 context1;
        private readonly ID3DUserDefinedAnnotation uda;

        // Cached resources
        private const int max_cached_uniform_buffers = 15;

        /// <summary>
        /// When true, bypasses all bind-state caching and SRV/UAV tracking.
        /// Always issues D3D11 bind calls unconditionally. Fixes deferred rendering
        /// pipelines where the SRV-conflict tracking causes incorrect unbinds.
        /// </summary>
        internal static bool AlwaysRebindResources = false;

        private readonly D3D11BufferRange[] vertexBoundUniformBuffers = new D3D11BufferRange[max_cached_uniform_buffers];
        private readonly D3D11BufferRange[] fragmentBoundUniformBuffers = new D3D11BufferRange[max_cached_uniform_buffers];
        private const int max_cached_texture_views = 16;
        private readonly D3D11TextureView[] vertexBoundTextureViews = new D3D11TextureView[max_cached_texture_views];
        private readonly D3D11TextureView[] fragmentBoundTextureViews = new D3D11TextureView[max_cached_texture_views];
        private const int max_cached_samplers = 4;
        private readonly D3D11Sampler[] vertexBoundSamplers = new D3D11Sampler[max_cached_samplers];
        private readonly D3D11Sampler[] fragmentBoundSamplers = new D3D11Sampler[max_cached_samplers];

        private readonly Dictionary<Texture, List<BoundTextureInfo>> boundSRVs = new Dictionary<Texture, List<BoundTextureInfo>>();
        private readonly Dictionary<Texture, List<BoundTextureInfo>> boundUaVs = new Dictionary<Texture, List<BoundTextureInfo>>();
        private readonly List<List<BoundTextureInfo>> boundTextureInfoPool = new List<List<BoundTextureInfo>>(20);

        private const int max_ua_vs = 8;
        private readonly List<(DeviceBuffer, int)> boundComputeUavBuffers = new List<(DeviceBuffer, int)>(max_ua_vs);
        private readonly List<(DeviceBuffer, int)> boundOmuavBuffers = new List<(DeviceBuffer, int)>(max_ua_vs);

        private readonly List<D3D11Buffer> availableStagingBuffers = new List<D3D11Buffer>();
        private readonly List<D3D11Buffer> submittedStagingBuffers = new List<D3D11Buffer>();

        private readonly List<D3D11Swapchain> referencedSwapchains = new List<D3D11Swapchain>();

        private D3D11Framebuffer d3D11Framebuffer => Util.AssertSubtype<Framebuffer, D3D11Framebuffer>(Framebuffer);
        private bool begun;
        private bool disposed;
        private ID3D11CommandList commandList;

        private Viewport[] viewports = new Viewport[0];
        private RawRect[] scissors = new RawRect[0];
        private bool viewportsChanged;
        private bool scissorRectsChanged;

        private uint numVertexBindings;
        private ID3D11Buffer[] vertexBindings = new ID3D11Buffer[1];
        private int[] vertexStrides = new int[1];
        private int[] vertexOffsets = new int[1];

        // Cached pipeline State
        private DeviceBuffer ib;
        private uint ibOffset;
        private ID3D11BlendState blendState;
        private Color4 blendFactor;
        private ID3D11DepthStencilState depthStencilState;
        private uint stencilReference;
        private ID3D11RasterizerState rasterizerState;
        private Vortice.Direct3D.PrimitiveTopology primitiveTopology;
        private ID3D11InputLayout inputLayout;
        private ID3D11VertexShader vertexShader;
        private ID3D11GeometryShader geometryShader;
        private ID3D11HullShader hullShader;
        private ID3D11DomainShader domainShader;
        private ID3D11PixelShader pixelShader;

        private D3D11Pipeline graphicsPipeline;

        // ── V11 (AN_Monsters spec 204 T2): SPIRV-Cross's host-filled `cbuffer SPIRV_Cross_VertexInfo { int BaseVertex; int BaseInstance; }` ──
        // D3D11's SV_VertexID / SV_InstanceID EXCLUDE the draw's base vertex / base instance; SPIR-V's gl_VertexIndex / gl_InstanceIndex
        // INCLUDE them. The cross-compiler adds this cbuffer to the vertex stage of any shader that reads either builtin and records its
        // register in the bundle (D3D11Pipeline.HlslVertexInfoRegister). Before EVERY draw of such a pipeline the two bases must be in it:
        // the first draw after a pipeline switch always writes (a fresh buffer's contents are undefined, so zero bases need writing too);
        // later draws write only when the bases change. One 16-byte dynamic cbuffer per command list; UpdateBuffer records in command order.
        private D3D11Buffer vertexInfoBuffer;
        private bool vertexInfoBoundForPipeline;
        private int vertexInfoLastBaseVertex;
        private uint vertexInfoLastBaseInstance;

        /// <summary>Mirror of SPIRV-Cross's <c>SPIRV_Cross_VertexInfo</c> cbuffer (two ints, padded to the 16-byte cbuffer minimum).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HlslVertexInfoCBuffer
        {
            public int SPIRV_Cross_BaseVertex;
            public int SPIRV_Cross_BaseInstance;
            public int _pad0, _pad1;
        }

        /// <summary>Write + bind the VertexInfo cbuffer for the current graphics pipeline if its vertex shader has one. Called by every Draw*Core.</summary>
        private void bindVertexInfoForDraw(int baseVertex, uint baseInstance)
        {
            int register = graphicsPipeline?.HlslVertexInfoRegister ?? -1;
            if (register < 0) return;

            bool needsWrite = !vertexInfoBoundForPipeline || vertexInfoLastBaseVertex != baseVertex || vertexInfoLastBaseInstance != baseInstance;
            if (!needsWrite) return;

            vertexInfoBuffer ??= Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(gd.ResourceFactory.CreateBuffer(
                new BufferDescription((uint)Unsafe.SizeOf<HlslVertexInfoCBuffer>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic)));
            var values = new HlslVertexInfoCBuffer { SPIRV_Cross_BaseVertex = baseVertex, SPIRV_Cross_BaseInstance = unchecked((int)baseInstance) };
            UpdateBuffer(vertexInfoBuffer, 0, ref values);

            // Bind at EXACTLY the compiler's register (V9). bindUniformBuffer's per-slot cache keeps it bound across draws; a resource
            // set can never occupy this register because the compiler chose it beyond every application cbuffer.
            bindUniformBuffer(new D3D11BufferRange(vertexInfoBuffer, 0, vertexInfoBuffer.SizeInBytes), register, ShaderStages.Vertex);
            vertexInfoBoundForPipeline = true;
            vertexInfoLastBaseVertex = baseVertex;
            vertexInfoLastBaseInstance = baseInstance;
        }

        /// <summary>
        /// Indirect draws (<see cref="DrawIndirectCore"/> / <see cref="DrawIndexedIndirectCore"/>) read their bases from the GPU buffer, where the
        /// host cannot see them: the cbuffer is written with ZERO bases so a shader that reads an index builtin sees exactly SV_*ID. This is why
        /// <see cref="GraphicsDeviceFeatures.DrawIndirectBaseInstance"/> is false on D3D11 - records must carry FirstInstance = 0 / VertexOffset = 0.
        /// </summary>
        private void bindVertexInfoForIndirectDraw() => bindVertexInfoForDraw(baseVertex: 0, baseInstance: 0);

        private BoundResourceSetInfo[] graphicsResourceSets = new BoundResourceSetInfo[1];

        // Resource sets are invalidated when a new resource set is bound with an incompatible SRV or UAV.
        private bool[] invalidatedGraphicsResourceSets = new bool[1];

        private D3D11Pipeline computePipeline;

        private BoundResourceSetInfo[] computeResourceSets = new BoundResourceSetInfo[1];

        // Resource sets are invalidated when a new resource set is bound with an incompatible SRV or UAV.
        private bool[] invalidatedComputeResourceSets = new bool[1];
        private string name;
        private bool vertexBindingsChanged;
        private readonly ID3D11Buffer[] cbOut = new ID3D11Buffer[1];
        private readonly int[] firstConstRef = new int[1];
        private readonly int[] numConstsRef = new int[1];

        public D3D11CommandList(D3D11GraphicsDevice gd, ref CommandListDescription description)
            : base(ref description, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
        {
            this.gd = gd;
            DeviceContext = gd.Device.CreateDeferredContext();
            context1 = DeviceContext.QueryInterfaceOrNull<ID3D11DeviceContext1>();
            uda = DeviceContext.QueryInterfaceOrNull<ID3DUserDefinedAnnotation>();
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                uda?.Dispose();
                DeviceCommandList?.Dispose();
                context1?.Dispose();
                DeviceContext.Dispose();

                foreach (var boundGraphicsSet in graphicsResourceSets) boundGraphicsSet.Offsets.Dispose();

                foreach (var boundComputeSet in computeResourceSets) boundComputeSet.Offsets.Dispose();

                foreach (var buffer in availableStagingBuffers) { StagingBufferPoolTelemetry.NoteEvictedFromPool(buffer.SizeInBytes); buffer.Dispose(); }
                availableStagingBuffers.Clear();

                disposed = true;
            }
        }

        #endregion

        public override void Begin()
        {
            commandList?.Dispose();
            commandList = null;
            clearState();
            emulatedRenderPassActive = false;
            if (currentDisjointWindow != null) closeDisjointWindow();   // Begin() after End() without a submit: the window still has to be closed

            // Parity with VkCommandList.Begin(): resolved slots become Fresh here (no GPU work on D3D11; the state machine is what the
            // "reused inside a render pass" rule reads).
            var pools = gd.TimestampPoolsSnapshot;
            for (int i = 0; i < pools.Length; i++)
                if (pools[i].HasSlotsAwaitingReset) pools[i].AdvanceResolvedSlotsToFresh();
            begun = true;
        }

        public override void End()
        {
            if (commandList != null) throw new VeldridException("Invalid use of End().");

            closeDisjointWindow();

            try
            {
                DeviceContext.FinishCommandList(false, out commandList).CheckError();
            }
            catch (SharpGenException nativeFailure)
            {
                // FinishCommandList is the first CheckError after a TDR in a typical frame — surface it typed
                // (AN_FluidUI crash log 2026-10-28: DXGI_ERROR_DEVICE_RESET escaped from exactly here).
                gd.rethrowAsDeviceLostIfApplicable(nativeFailure);
                throw;
            }
            commandList.DebugName = name;
            resetManagedState();
            begun = false;
        }

        public void Reset()
        {
            closeDisjointWindow();

            if (commandList != null)
            {
                commandList.Dispose();
                commandList = null;
            }
            else if (begun)
            {
                DeviceContext.ClearState();
                DeviceContext.FinishCommandList(false, out commandList);
                commandList.Dispose();
                commandList = null;
            }

            resetManagedState();
            begun = false;
        }

        #region Timestamp queries (_TASKS/GpuTimestampQueries.md 5.1 / 5.3)

        /// <summary>
        ///     Mirror of Vulkan's lazy render pass so the "reused slot inside a pass" rule throws in the SAME situations on both backends:
        ///     set by draws only - NOT clears, which Vulkan defers into the pass's load-op (Vulkan begins its pass there), cleared by SetFramebuffer / Dispatch / Copy* / Resolve / Begin / End
        ///     (Vulkan ends its pass there). D3D11 itself has no such restriction.
        /// </summary>
        private bool emulatedRenderPassActive;

        /// <summary>The D3D11_QUERY_TIMESTAMP_DISJOINT bracket of the current recording; opened on the first WriteTimestamp, closed at End()/Reset().</summary>
        private D3D11TimestampDisjointWindow currentDisjointWindow;

        private void closeDisjointWindow()
        {
            if (currentDisjointWindow == null) return;
            DeviceContext.End(currentDisjointWindow.Query);
            var window = currentDisjointWindow;
            currentDisjointWindow = null;
            window.Ended = true;
            // All slots that referenced it may already have been rewritten (abandoned) -> nothing holds it; recycle now.
            if (window.SlotReferences == 0) gd.ReturnTimestampDisjointWindow(window);
        }

        private protected override void WriteTimestampCore(TimestampQueryPool pool, uint index)
        {
            var d3dPool = Util.AssertSubtype<TimestampQueryPool, D3D11TimestampQueryPool>(pool);

            if (currentDisjointWindow == null)
            {
                currentDisjointWindow = gd.RentTimestampDisjointWindow();
                DeviceContext.Begin(currentDisjointWindow.Query);   // on the DEFERRED context: the bracket executes with the command list
            }

            D3D11TimestampQueryPool.SlotState previous;
            D3D11TimestampDisjointWindow previousWindow;
            lock (d3dPool.StateLock) d3dPool.PeekLocked(index, out previous, out previousWindow);

            if (previous != D3D11TimestampQueryPool.SlotState.Fresh && emulatedRenderPassActive)
            {
                // D3D11 could legally End() the query again here; refusing keeps the contract identical to Vulkan (VkCommandList.WriteTimestampCore).
#if VALIDATE_USAGE
                throw new VeldridException(
                    $"WriteTimestamp: slot {index} of TimestampQueryPool '{pool.Name}' is being reused inside a render pass " +
                    $"(previous state {previous}). A reused slot must be reset first, which is only legal outside a render pass: " +
                    "resolve the pool before the next CommandList.Begin() (the reset is then recorded automatically), write the first " +
                    "timestamp of this pool before SetFramebuffer/draws, or use a deeper ring of pools.");
#else
                return;
#endif
            }

            d3dPool.MarkWritten(index, currentDisjointWindow);
            DeviceContext.End(d3dPool.Queries[index]);   // D3D11 timestamps are End()-only (no Begin); the value is taken when the GPU reaches this point
        }

        #endregion

        private protected override void DispatchCore(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            preDispatchCommand();

            DeviceContext.Dispatch((int)groupCountX, (int)groupCountY, (int)groupCountZ);
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            scissorRectsChanged = true;
            Util.EnsureArrayMinimumSize(ref scissors, index + 1);
            scissors[index] = new RawRect((int)x, (int)y, (int)(x + width), (int)(y + height));
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            viewportsChanged = true;
            Util.EnsureArrayMinimumSize(ref viewports, index + 1);
            viewports[index] = viewport;
        }

        internal void OnCompleted()
        {
            commandList.Dispose();
            commandList = null;

            foreach (var sc in referencedSwapchains) sc.RemoveCommandListReference(this);
            referencedSwapchains.Clear();

            foreach (var buffer in submittedStagingBuffers) { availableStagingBuffers.Add(buffer); StagingBufferPoolTelemetry.NoteReturnedToPool(buffer.SizeInBytes); }

            submittedStagingBuffers.Clear();
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, ReadOnlySpan<uint> dynamicOffsets)
        {
            if (graphicsResourceSets[slot].Equals(rs, dynamicOffsets)) return;

            graphicsResourceSets[slot].Offsets.Dispose();
            graphicsResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsets);
            activateResourceSet(slot, graphicsResourceSets[slot], true);
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet set, ReadOnlySpan<uint> dynamicOffsets)
        {
            if (computeResourceSets[slot].Equals(set, dynamicOffsets)) return;

            computeResourceSets[slot].Offsets.Dispose();
            computeResourceSets[slot] = new BoundResourceSetInfo(set, dynamicOffsets);
            activateResourceSet(slot, computeResourceSets[slot], false);
        }

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            preDrawCommand();
            bindVertexInfoForIndirectDraw();

            var d3d11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(indirectBuffer);
            int currentOffset = (int)offset;

            for (uint i = 0; i < drawCount; i++)
            {
                DeviceContext.DrawInstancedIndirect(d3d11Buffer.Buffer, currentOffset);
                currentOffset += (int)stride;
            }
        }

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            preDrawCommand();
            bindVertexInfoForIndirectDraw();

            var d3d11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(indirectBuffer);
            int currentOffset = (int)offset;

            for (uint i = 0; i < drawCount; i++)
            {
                DeviceContext.DrawIndexedInstancedIndirect(d3d11Buffer.Buffer, currentOffset);
                currentOffset += (int)stride;
            }
        }

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        {
            preDispatchCommand();
            var d3d11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(indirectBuffer);
            DeviceContext.DispatchIndirect(d3d11Buffer.Buffer, (int)offset);
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            emulatedRenderPassActive = false;
            var d3d11Source = Util.AssertSubtype<Texture, D3D11Texture>(source);
            var d3d11Destination = Util.AssertSubtype<Texture, D3D11Texture>(destination);
            DeviceContext.ResolveSubresource(
                d3d11Destination.DeviceTexture,
                0,
                d3d11Source.DeviceTexture,
                0,
                d3d11Destination.DxgiFormat);
        }

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            emulatedRenderPassActive = false;
            var d3dFb = Util.AssertSubtype<Framebuffer, D3D11Framebuffer>(fb);

            if (d3dFb.Swapchain != null)
            {
                d3dFb.Swapchain.AddCommandListReference(this);
                referencedSwapchains.Add(d3dFb.Swapchain);
            }

            for (int i = 0; i < fb.ColorTargets.Count; i++) unbindSrvTexture(fb.ColorTargets[i].Target);

            DeviceContext.OMSetRenderTargets(d3dFb.RenderTargetViews, d3dFb.DepthStencilView);
        }

        protected override void CopyBufferCore(DeviceBuffer source, uint sourceOffset, DeviceBuffer destination, uint destinationOffset, uint sizeInBytes)
        {
            emulatedRenderPassActive = false;
            var srcD3D11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(source);
            var dstD3D11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(destination);

            var region = new Box((int)sourceOffset, 0, 0, (int)(sourceOffset + sizeInBytes), 1, 1);

            DeviceContext.CopySubresourceRegion(dstD3D11Buffer.Buffer, 0, (int)destinationOffset, 0, 0, srcD3D11Buffer.Buffer, 0, region);
        }

        protected override void CopyTextureCore(
            Texture source,
            uint srcX, uint srcY, uint srcZ,
            uint srcMipLevel,
            uint srcBaseArrayLayer,
            Texture destination,
            uint dstX, uint dstY, uint dstZ,
            uint dstMipLevel,
            uint dstBaseArrayLayer,
            uint width, uint height, uint depth,
            uint layerCount)
        {
            emulatedRenderPassActive = false;
            var srcD3D11Texture = Util.AssertSubtype<Texture, D3D11Texture>(source);
            var dstD3D11Texture = Util.AssertSubtype<Texture, D3D11Texture>(destination);

            uint blockSize = FormatHelpers.IsCompressedFormat(source.Format) ? 4u : 1u;
            uint clampedWidth = Math.Max(blockSize, width);
            uint clampedHeight = Math.Max(blockSize, height);

            Box? region = null;

            if (srcX != 0 || srcY != 0 || srcZ != 0
                || clampedWidth != source.Width || clampedHeight != source.Height || depth != source.Depth)
            {
                region = new Box(
                    (int)srcX,
                    (int)srcY,
                    (int)srcZ,
                    (int)(srcX + clampedWidth),
                    (int)(srcY + clampedHeight),
                    (int)(srcZ + depth));
            }

            for (uint i = 0; i < layerCount; i++)
            {
                int srcSubresource = D3D11Util.ComputeSubresource(srcMipLevel, source.MipLevels, srcBaseArrayLayer + i);
                int dstSubresource = D3D11Util.ComputeSubresource(dstMipLevel, destination.MipLevels, dstBaseArrayLayer + i);

                DeviceContext.CopySubresourceRegion(
                    dstD3D11Texture.DeviceTexture,
                    dstSubresource,
                    (int)dstX,
                    (int)dstY,
                    (int)dstZ,
                    srcD3D11Texture.DeviceTexture,
                    srcSubresource,
                    region);
            }
        }

        private void clearState()
        {
            ClearCachedState();
            DeviceContext.ClearState();
            resetManagedState();
        }

        private void resetManagedState()
        {
            numVertexBindings = 0;
            vertexInfoBoundForPipeline = false;
            Util.ClearArray(vertexBindings);
            Util.ClearArray(vertexStrides);
            Util.ClearArray(vertexOffsets);

            Framebuffer = null;

            Util.ClearArray(viewports);
            Util.ClearArray(scissors);
            viewportsChanged = false;
            scissorRectsChanged = false;

            ib = null;
            graphicsPipeline = null;
            blendState = null;
            depthStencilState = null;
            rasterizerState = null;
            primitiveTopology = Vortice.Direct3D.PrimitiveTopology.Undefined;
            inputLayout = null;
            vertexShader = null;
            geometryShader = null;
            hullShader = null;
            domainShader = null;
            pixelShader = null;

            clearSets(graphicsResourceSets);

            Util.ClearArray(vertexBoundUniformBuffers);
            Util.ClearArray(vertexBoundTextureViews);
            Util.ClearArray(vertexBoundSamplers);

            Util.ClearArray(fragmentBoundUniformBuffers);
            Util.ClearArray(fragmentBoundTextureViews);
            Util.ClearArray(fragmentBoundSamplers);

            computePipeline = null;
            clearSets(computeResourceSets);

            foreach (var kvp in boundSRVs)
            {
                var list = kvp.Value;
                list.Clear();
                poolBoundTextureList(list);
            }

            boundSRVs.Clear();

            foreach (var kvp in boundUaVs)
            {
                var list = kvp.Value;
                list.Clear();
                poolBoundTextureList(list);
            }

            boundUaVs.Clear();

            // The buffer-UAV mirrors must reset with everything else: every caller has already reset the device context
            // (ClearState on Begin/Reset, FinishCommandList(false) on End), so no CSUnset/OMUnset is needed here - just forget.
            // Omitting this made boundComputeUavBuffers grow by one entry per buffer UAV per dispatch for the life of the command
            // list (_BUGFIX/BUGFIX_D3D11_ComputeUavBindingList_Grows_PerFrame.md).
            boundComputeUavBuffers.Clear();
            boundOmuavBuffers.Clear();
        }

        private void clearSets(BoundResourceSetInfo[] boundSets)
        {
            foreach (var boundSetInfo in boundSets) boundSetInfo.Offsets.Dispose();
            Util.ClearArray(boundSets);
        }

        private void activateResourceSet(uint slot, BoundResourceSetInfo brsi, bool graphics)
        {
            var d3d11Rs = Util.AssertSubtype<ResourceSet, D3D11ResourceSet>(brsi.Set);
            // THE binding rule: a resource declared at GLSL (set, binding) lands where THIS pipeline's compiled shader reads it. Only the
            // pipeline's own layout knows that register (SPIRV-Cross numbers D3D11 registers per kind across ALL sets of one shader, so
            // the same (set, binding) can be b1 in one shader and b2 in another). The set's layout is the layout of whatever shape the
            // set was CREATED from; its declarations are identical (ShaderPipelineShape verifies that at bind), its registers may not be.
            // V9: every register is the compiler's flat-binding-map value, absolute and global - no cross-set base is ever added.
            var pipelineLayouts = graphics ? graphicsPipeline?.ResourceLayouts : computePipeline?.ResourceLayouts;
            var layout = pipelineLayouts != null && slot < pipelineLayouts.Length && pipelineLayouts[slot] != null
                ? pipelineLayouts[slot]
                : d3d11Rs.Layout;

            var resources = d3d11Rs.Resources;
            uint dynamicOffsetIndex = 0;

            for (int i = 0; i < resources.Length; i++)
            {
                var resource = resources[i];
                uint bufferOffset = 0;

                if (layout.IsDynamicBuffer(i))
                {
                    bufferOffset = brsi.Offsets.Get(dynamicOffsetIndex);
                    dynamicOffsetIndex += 1;
                }

                var rbi = layout.GetDeviceSlotIndex(i);

                switch (rbi.Kind)
                {
                    case ResourceKind.UniformBuffer:
                    {
                        var range = getBufferRange(resource, bufferOffset);
                        bindUniformBuffer(range, rbi.Slot, rbi.Stages);
                        break;
                    }

                    case ResourceKind.StructuredBufferReadOnly:
                    {
                        var range = getBufferRange(resource, bufferOffset);
                        bindStorageBufferView(range, rbi.Slot, rbi.Stages);
                        break;
                    }

                    case ResourceKind.StructuredBufferReadWrite:
                    {
                        var range = getBufferRange(resource, bufferOffset);
                        var uav = range.Buffer.GetUnorderedAccessView(range.Offset, range.Size);
                        bindUnorderedAccessView(null, range.Buffer, uav, rbi.Slot, rbi.Stages, slot);
                        break;
                    }

                    case ResourceKind.TextureReadOnly:
                        var texView = Util.GetTextureView(gd, resource);
                        var d3d11TexView = Util.AssertSubtype<TextureView, D3D11TextureView>(texView);
                        unbindUavTexture(d3d11TexView.Target);
                        bindTextureView(d3d11TexView, rbi.Slot, rbi.Stages, slot);
                        break;

                    case ResourceKind.TextureReadWrite:
                        var rwTexView = Util.GetTextureView(gd, resource);
                        var d3d11RwTexView = Util.AssertSubtype<TextureView, D3D11TextureView>(rwTexView);
                        unbindSrvTexture(d3d11RwTexView.Target);
                        bindUnorderedAccessView(d3d11RwTexView.Target, null, d3d11RwTexView.UnorderedAccessView, rbi.Slot, rbi.Stages, slot);
                        break;

                    case ResourceKind.Sampler:
                        var sampler = Util.AssertSubtype<IBindableResource, D3D11Sampler>(resource);
                        bindSampler(sampler, rbi.Slot, rbi.Stages);
                        break;

                    default: throw Illegal.Value<ResourceKind>();
                }
            }
        }

        private D3D11BufferRange getBufferRange(IBindableResource resource, uint additionalOffset)
        {
            if (resource is D3D11Buffer d3d11Buff)
                return new D3D11BufferRange(d3d11Buff, additionalOffset, d3d11Buff.SizeInBytes);

            if (resource is DeviceBufferRange range)
            {
                return new D3D11BufferRange(
                    Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(range.Buffer),
                    range.Offset + additionalOffset,
                    range.SizeInBytes);
            }

            throw new VeldridException($"Unexpected resource type used in a buffer type slot: {resource.GetType().Name}");
        }

        private void unbindSrvTexture(Texture target)
        {
            if (AlwaysRebindResources) return;

            if (boundSRVs.TryGetValue(target, out var btis))
            {
                foreach (var bti in btis)
                {
                    bindTextureView(null, bti.Slot, bti.Stages, 0);

                    if ((bti.Stages & ShaderStages.Compute) == ShaderStages.Compute)
                        invalidatedComputeResourceSets[bti.ResourceSet] = true;
                    else
                        invalidatedGraphicsResourceSets[bti.ResourceSet] = true;
                }

                bool result = boundSRVs.Remove(target);
                Debug.Assert(result);

                btis.Clear();
                poolBoundTextureList(btis);
            }
        }

        private void poolBoundTextureList(List<BoundTextureInfo> btis)
        {
            boundTextureInfoPool.Add(btis);
        }

        private void unbindUavTexture(Texture target)
        {
            if (AlwaysRebindResources) return;

            if (boundUaVs.TryGetValue(target, out var btis))
            {
                foreach (var bti in btis)
                {
                    bindUnorderedAccessView(null, null, null, bti.Slot, bti.Stages, bti.ResourceSet);
                    if ((bti.Stages & ShaderStages.Compute) == ShaderStages.Compute)
                        invalidatedComputeResourceSets[bti.ResourceSet] = true;
                    else
                        invalidatedGraphicsResourceSets[bti.ResourceSet] = true;
                }

                bool result = boundUaVs.Remove(target);
                Debug.Assert(result);

                btis.Clear();
                poolBoundTextureList(btis);
            }
        }

        private void preDrawCommand()
        {
            emulatedRenderPassActive = true;
            flushViewports();
            flushScissorRects();
            flushVertexBindings();

            int graphicsResourceCount = graphicsPipeline.ResourceLayouts.Length;

            for (uint i = 0; i < graphicsResourceCount; i++)
            {
                if (invalidatedGraphicsResourceSets[i])
                {
                    invalidatedGraphicsResourceSets[i] = false;
                    activateResourceSet(i, graphicsResourceSets[i], true);
                }
            }
        }

        private void preDispatchCommand()
        {
            emulatedRenderPassActive = false;
            int computeResourceCount = computePipeline.ResourceLayouts.Length;

            for (uint i = 0; i < computeResourceCount; i++)
            {
                if (invalidatedComputeResourceSets[i])
                {
                    invalidatedComputeResourceSets[i] = false;
                    activateResourceSet(i, computeResourceSets[i], false);
                }
            }
        }

        private void flushViewports()
        {
            if (viewportsChanged)
            {
                viewportsChanged = false;
                DeviceContext.RSSetViewports(viewports);
            }
        }

        private void flushScissorRects()
        {
            if (scissorRectsChanged)
            {
                scissorRectsChanged = false;

                if (scissors.Length > 0)
                {
                    // Because this array is resized using Util.EnsureMinimumArraySize, this might set more scissor rectangles
                    // than are actually needed, but this is okay -- extras are essentially ignored and should be harmless.
                    DeviceContext.RSSetScissorRects(scissors);
                }
            }
        }

        private void flushVertexBindings()
        {
            if (vertexBindingsChanged)
            {
                DeviceContext.IASetVertexBuffers(
                    0, (int)numVertexBindings,
                    vertexBindings,
                    vertexStrides,
                    vertexOffsets);

                vertexBindingsChanged = false;
            }
        }

        private void bindTextureView(D3D11TextureView texView, int slot, ShaderStages stages, uint resourceSet)
        {
            var srv = texView?.ShaderResourceView;

            if (!AlwaysRebindResources && srv != null)
            {
                if (!boundSRVs.TryGetValue(texView.Target, out var list))
                {
                    list = getNewOrCachedBoundTextureInfoList();
                    boundSRVs.Add(texView.Target, list);
                }

                list.Add(new BoundTextureInfo { Slot = slot, Stages = stages, ResourceSet = resourceSet });
            }

            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex)
            {
                if (AlwaysRebindResources)
                    DeviceContext.VSSetShaderResource(slot, srv);
                else if (slot >= max_cached_texture_views || vertexBoundTextureViews[slot] != texView)
                {
                    if (slot < max_cached_texture_views) vertexBoundTextureViews[slot] = texView;
                    DeviceContext.VSSetShaderResource(slot, srv);
                }
            }

            if ((stages & ShaderStages.Geometry) == ShaderStages.Geometry) DeviceContext.GSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.TessellationControl) == ShaderStages.TessellationControl) DeviceContext.HSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.TessellationEvaluation) == ShaderStages.TessellationEvaluation) DeviceContext.DSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment)
            {
                if (AlwaysRebindResources)
                    DeviceContext.PSSetShaderResource(slot, srv!);
                else if (slot >= max_cached_texture_views || fragmentBoundTextureViews[slot] != texView)
                {
                    if (slot < max_cached_texture_views) fragmentBoundTextureViews[slot] = texView;
                    DeviceContext.PSSetShaderResource(slot, srv!);
                }
            }

            if ((stages & ShaderStages.Compute) == ShaderStages.Compute) DeviceContext.CSSetShaderResource(slot, srv);
        }

        private List<BoundTextureInfo> getNewOrCachedBoundTextureInfoList()
        {
            if (boundTextureInfoPool.Count > 0)
            {
                int index = boundTextureInfoPool.Count - 1;
                var ret = boundTextureInfoPool[index];
                boundTextureInfoPool.RemoveAt(index);
                return ret;
            }

            return new List<BoundTextureInfo>();
        }

        private void bindStorageBufferView(D3D11BufferRange range, int slot, ShaderStages stages)
        {
            bool compute = (stages & ShaderStages.Compute) != 0;
            unbindUavBuffer(range.Buffer);

            var srv = range.Buffer.GetShaderResourceView(range.Offset, range.Size);

            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex) DeviceContext.VSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.Geometry) == ShaderStages.Geometry) DeviceContext.GSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.TessellationControl) == ShaderStages.TessellationControl) DeviceContext.HSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.TessellationEvaluation) == ShaderStages.TessellationEvaluation) DeviceContext.DSSetShaderResource(slot, srv);

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment) DeviceContext.PSSetShaderResource(slot, srv);

            if (compute) DeviceContext.CSSetShaderResource(slot, srv);
        }

        private void bindUniformBuffer(D3D11BufferRange range, int slot, ShaderStages stages)
        {
            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex)
            {
                bool bind = AlwaysRebindResources;

                if (!bind && slot < max_cached_uniform_buffers)
                {
                    if (!vertexBoundUniformBuffers[slot].Equals(range))
                    {
                        vertexBoundUniformBuffers[slot] = range;
                        bind = true;
                    }
                }
                else if (!bind)
                    bind = true;

                if (bind)
                {
                    if (range.IsFullRange)
                        DeviceContext.VSSetConstantBuffer(slot, range.Buffer.Buffer);
                    else
                    {
                        packRangeParams(range);
                        if (!gd.SupportsCommandLists) DeviceContext.VSUnsetConstantBuffer(slot);
                        context1.VSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                    }
                }
            }

            if ((stages & ShaderStages.Geometry) == ShaderStages.Geometry)
            {
                if (range.IsFullRange)
                    DeviceContext.GSSetConstantBuffer(slot, range.Buffer.Buffer);
                else
                {
                    packRangeParams(range);
                    if (!gd.SupportsCommandLists) DeviceContext.GSUnsetConstantBuffer(slot);
                    context1.GSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                }
            }

            if ((stages & ShaderStages.TessellationControl) == ShaderStages.TessellationControl)
            {
                if (range.IsFullRange)
                    DeviceContext.HSSetConstantBuffer(slot, range.Buffer.Buffer);
                else
                {
                    packRangeParams(range);
                    if (!gd.SupportsCommandLists) DeviceContext.HSUnsetConstantBuffer(slot);
                    context1.HSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                }
            }

            if ((stages & ShaderStages.TessellationEvaluation) == ShaderStages.TessellationEvaluation)
            {
                if (range.IsFullRange)
                    DeviceContext.DSSetConstantBuffer(slot, range.Buffer.Buffer);
                else
                {
                    packRangeParams(range);
                    if (!gd.SupportsCommandLists) DeviceContext.DSUnsetConstantBuffer(slot);
                    context1.DSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                }
            }

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment)
            {
                bool bind = AlwaysRebindResources;

                if (!bind && slot < max_cached_uniform_buffers)
                {
                    if (!fragmentBoundUniformBuffers[slot].Equals(range))
                    {
                        fragmentBoundUniformBuffers[slot] = range;
                        bind = true;
                    }
                }
                else if (!bind)
                    bind = true;

                if (bind)
                {
                    if (range.IsFullRange)
                        DeviceContext.PSSetConstantBuffer(slot, range.Buffer.Buffer);
                    else
                    {
                        packRangeParams(range);
                        if (!gd.SupportsCommandLists) DeviceContext.PSUnsetConstantBuffer(slot);
                        context1.PSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                    }
                }
            }

            if ((stages & ShaderStages.Compute) == ShaderStages.Compute)
            {
                if (range.IsFullRange)
                    DeviceContext.CSSetConstantBuffer(slot, range.Buffer.Buffer);
                else
                {
                    packRangeParams(range);
                    if (!gd.SupportsCommandLists) DeviceContext.CSSetConstantBuffer(slot, null);
                    context1.CSSetConstantBuffers1(slot, 1, cbOut, firstConstRef, numConstsRef);
                }
            }
        }

        private void packRangeParams(D3D11BufferRange range)
        {
            cbOut[0] = range.Buffer.Buffer;
            firstConstRef[0] = (int)range.Offset / 16;
            uint roundedSize = range.Size < 256 ? 256u : range.Size;
            numConstsRef[0] = (int)roundedSize / 16;
        }

        private void bindUnorderedAccessView(
            Texture texture,
            DeviceBuffer buffer,
            ID3D11UnorderedAccessView uav,
            int slot,
            ShaderStages stages,
            uint resourceSet)
        {
            bool compute = stages == ShaderStages.Compute;
            Debug.Assert(compute || (stages & ShaderStages.Compute) == 0);
            Debug.Assert(texture == null || buffer == null);

            if (texture != null && uav != null)
            {
                if (!boundUaVs.TryGetValue(texture, out var list))
                {
                    list = getNewOrCachedBoundTextureInfoList();
                    boundUaVs.Add(texture, list);
                }

                list.Add(new BoundTextureInfo { Slot = slot, Stages = stages, ResourceSet = resourceSet });
            }

            // V9: `slot` IS the u# register the shader compiler assigned (flat binding map). Nothing is added here - the old
            // "+ Framebuffer.ColorTargets.Count" was a second guess stacked on the compiler's answer. The compiler already starts
            // pixel-shader UAVs above the shader's output count (fxc requires it), and D3D11Pipeline checks that against the
            // pipeline's colour attachments at creation.
            if (buffer != null) trackBoundUavBuffer(buffer, slot, compute);

            if (compute)
                DeviceContext.CSSetUnorderedAccessView(slot, uav);
            else
                DeviceContext.OMSetUnorderedAccessView(slot, uav!);
        }

        private void trackBoundUavBuffer(DeviceBuffer buffer, int slot, bool compute)
        {
            var list = compute ? boundComputeUavBuffers : boundOmuavBuffers;

            // A UAV slot holds exactly one view, so the mirror holds at most one entry per slot: REPLACE a stale entry for this slot
            // instead of appending. Appending unconditionally (the pre-Nov-2026 behaviour) had two faults
            // (_BUGFIX/BUGFIX_D3D11_ComputeUavBindingList_Grows_PerFrame.md):
            //   1. the list grew by N per dispatch, forever (resetManagedState never cleared it), and every later SetVertexBuffer /
            //      SetIndexBuffer / buffer-SRV bind scanned it - O(frames x draws) CPU decay;
            //   2. a stale (oldBuffer, slot) entry survived a rebind of that slot, so a later vertex/index bind of oldBuffer would
            //      CSUnset/OMUnset the slot now holding a DIFFERENT buffer.
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Item2 == slot)
                {
                    list[i] = (buffer, slot);
                    return;
                }
            }

            list.Add((buffer, slot));
        }

        /// <summary>
        ///     Test seam (Veldrid.ContractTests, InternalsVisibleTo): the number of buffer UAVs the managed mirror believes are bound on
        ///     the compute (<c>CSSetUnorderedAccessView</c>) or output-merger (<c>OMSetUnorderedAccessView</c>) stage. Bounded by the
        ///     UAV slot count (<see cref="max_ua_vs"/>) within one command list and zero after <see cref="End"/> / <see cref="Begin"/>.
        /// </summary>
        internal int DebugTrackedUavBufferCount(bool compute) => (compute ? boundComputeUavBuffers : boundOmuavBuffers).Count;

        /// <summary>
        ///     Test seam: records <paramref name="buffer"/> as the buffer UAV in <paramref name="slot"/> exactly as a compute /
        ///     graphics resource-set bind does (the same private path, no device call). Lets a test exercise the mirror's bounding and
        ///     reset rules without a compiled compute bundle.
        /// </summary>
        internal void DebugTrackUavBufferForTests(DeviceBuffer buffer, int slot, bool compute)
        {
            trackBoundUavBuffer(buffer, slot, compute);
        }

        private void unbindUavBuffer(DeviceBuffer buffer)
        {
            unbindUavBufferIndividual(buffer, false);
            unbindUavBufferIndividual(buffer, true);
        }

        private void unbindUavBufferIndividual(DeviceBuffer buffer, bool compute)
        {
            var list = compute ? boundComputeUavBuffers : boundOmuavBuffers;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Item1 == buffer)
                {
                    int slot = list[i].Item2;
                    if (compute)
                        DeviceContext.CSUnsetUnorderedAccessView(slot);
                    else
                        DeviceContext.OMUnsetUnorderedAccessView(slot);

                    list.RemoveAt(i);
                    i -= 1;
                }
            }
        }

        private void bindSampler(D3D11Sampler sampler, int slot, ShaderStages stages)
        {
            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex)
            {
                if (AlwaysRebindResources)
                    DeviceContext.VSSetSampler(slot, sampler.DeviceSampler);
                else if (slot >= max_cached_samplers || vertexBoundSamplers[slot] != sampler)
                {
                    if (slot < max_cached_samplers) vertexBoundSamplers[slot] = sampler;
                    DeviceContext.VSSetSampler(slot, sampler.DeviceSampler);
                }
            }

            if ((stages & ShaderStages.Geometry) == ShaderStages.Geometry) DeviceContext.GSSetSampler(slot, sampler.DeviceSampler);

            if ((stages & ShaderStages.TessellationControl) == ShaderStages.TessellationControl) DeviceContext.HSSetSampler(slot, sampler.DeviceSampler);

            if ((stages & ShaderStages.TessellationEvaluation) == ShaderStages.TessellationEvaluation) DeviceContext.DSSetSampler(slot, sampler.DeviceSampler);

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment)
            {
                if (AlwaysRebindResources)
                    DeviceContext.PSSetSampler(slot, sampler.DeviceSampler);
                else if (slot >= max_cached_samplers || fragmentBoundSamplers[slot] != sampler)
                {
                    if (slot < max_cached_samplers) fragmentBoundSamplers[slot] = sampler;
                    DeviceContext.PSSetSampler(slot, sampler.DeviceSampler);
                }
            }

            if ((stages & ShaderStages.Compute) == ShaderStages.Compute) DeviceContext.CSSetSampler(slot, sampler.DeviceSampler);
        }

        private unsafe void UpdateSubresource_Workaround(
            ID3D11Resource resource,
            int subresource,
            Box? region,
            IntPtr data)
        {
            bool needWorkaround = !gd.SupportsCommandLists;
            var pAdjustedSrcData = data.ToPointer();

            if (needWorkaround && region is Box dstRegion)
            {
                Debug.Assert(dstRegion.Top == 0 && dstRegion.Front == 0);
                pAdjustedSrcData = (byte*)data - dstRegion.Left;
            }

            DeviceContext.UpdateSubresource(resource, subresource, region, (IntPtr)pAdjustedSrcData, 0, 0);
        }

        private D3D11Buffer getFreeStagingBuffer(uint sizeInBytes)
        {
            // Best fit: the SMALLEST pooled buffer that is large enough.
            D3D11Buffer bestFit = null;
            foreach (var buffer in availableStagingBuffers)
            {
                if (buffer.SizeInBytes >= sizeInBytes && (bestFit == null || buffer.SizeInBytes < bestFit.SizeInBytes))
                    bestFit = buffer;
            }
            if (bestFit != null)
            {
                availableStagingBuffers.Remove(bestFit);
                StagingBufferPoolTelemetry.NoteTakenFromPool(bestFit.SizeInBytes);
                return bestFit;
            }

            // Miss: every pooled buffer is smaller than this request. Retire them all — the buffer created below
            // can serve any request they could. Without this the pool grew by one exact-size CPU buffer for every
            // new high-water mark and never shrank (a frame whose vertex count grows a little each redraw — a live
            // graph filling its history — leaked ~1 MB per redraw for half an hour: AN_Monitor, 2026-09-24, 2 GB).
            foreach (var buffer in availableStagingBuffers) { StagingBufferPoolTelemetry.NoteEvictedFromPool(buffer.SizeInBytes); buffer.Dispose(); }
            availableStagingBuffers.Clear();

            var staging = gd.ResourceFactory.CreateBuffer(
                new BufferDescription(sizeInBytes, BufferUsage.Staging, structureByteStride: 0, rawBuffer: false, allowUpdateBufferOutsideCommandList: true /* backend-owned pool: fill -> record copy -> recycle after completion */));
            StagingBufferPoolTelemetry.NoteCreated(sizeInBytes);

            return Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(staging);
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            if (ib != buffer || ibOffset != offset)
            {
                ib = buffer;
                ibOffset = offset;
                var d3d11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(buffer);
                unbindUavBuffer(buffer);
                DeviceContext.IASetIndexBuffer(d3d11Buffer.Buffer, D3D11Formats.ToDxgiFormat(format), (int)offset);
            }
        }

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            if (!pipeline.IsComputePipeline && graphicsPipeline != pipeline)
            {
                var d3dPipeline = Util.AssertSubtype<Pipeline, D3D11Pipeline>(pipeline);
                graphicsPipeline = d3dPipeline;
                vertexInfoBoundForPipeline = false;   // V11: the new pipeline's VertexInfo cbuffer (if any) must be written + bound before its first draw
                clearSets(graphicsResourceSets); // Invalidate resource set bindings -- they may be invalid.
                Util.ClearArray(invalidatedGraphicsResourceSets);

                if (blendState != d3dPipeline.BlendState || blendFactor != d3dPipeline.BlendFactor)
                {
                    blendState = d3dPipeline.BlendState;
                    blendFactor = d3dPipeline.BlendFactor;
                    DeviceContext.OMSetBlendState(blendState, blendFactor);
                }

                if (depthStencilState != d3dPipeline.DepthStencilState || stencilReference != d3dPipeline.StencilReference)
                {
                    depthStencilState = d3dPipeline.DepthStencilState;
                    stencilReference = d3dPipeline.StencilReference;
                    DeviceContext.OMSetDepthStencilState(depthStencilState, (int)stencilReference);
                }

                if (rasterizerState != d3dPipeline.RasterizerState)
                {
                    rasterizerState = d3dPipeline.RasterizerState;
                    DeviceContext.RSSetState(rasterizerState);
                }

                if (primitiveTopology != d3dPipeline.PrimitiveTopology)
                {
                    primitiveTopology = d3dPipeline.PrimitiveTopology;
                    DeviceContext.IASetPrimitiveTopology(primitiveTopology);
                }

                if (inputLayout != d3dPipeline.InputLayout)
                {
                    inputLayout = d3dPipeline.InputLayout;
                    DeviceContext.IASetInputLayout(inputLayout);
                }

                if (vertexShader != d3dPipeline.VertexShader)
                {
                    vertexShader = d3dPipeline.VertexShader;
                    DeviceContext.VSSetShader(vertexShader);
                }

                if (geometryShader != d3dPipeline.GeometryShader)
                {
                    geometryShader = d3dPipeline.GeometryShader;
                    DeviceContext.GSSetShader(geometryShader);
                }

                if (hullShader != d3dPipeline.HullShader)
                {
                    hullShader = d3dPipeline.HullShader;
                    DeviceContext.HSSetShader(hullShader);
                }

                if (domainShader != d3dPipeline.DomainShader)
                {
                    domainShader = d3dPipeline.DomainShader;
                    DeviceContext.DSSetShader(domainShader);
                }

                if (pixelShader != d3dPipeline.PixelShader)
                {
                    pixelShader = d3dPipeline.PixelShader;
                    DeviceContext.PSSetShader(pixelShader);
                }

                if (!Util.ArrayEqualsEquatable(vertexStrides, d3dPipeline.VertexStrides))
                {
                    vertexBindingsChanged = true;

                    if (d3dPipeline.VertexStrides != null)
                    {
                        Util.EnsureArrayMinimumSize(ref vertexStrides, (uint)d3dPipeline.VertexStrides.Length);
                        d3dPipeline.VertexStrides.CopyTo(vertexStrides, 0);
                    }
                }

                Util.EnsureArrayMinimumSize(ref vertexStrides, 1);
                Util.EnsureArrayMinimumSize(ref vertexBindings, (uint)vertexStrides.Length);
                Util.EnsureArrayMinimumSize(ref vertexOffsets, (uint)vertexStrides.Length);

                Util.EnsureArrayMinimumSize(ref graphicsResourceSets, (uint)d3dPipeline.ResourceLayouts.Length);
                Util.EnsureArrayMinimumSize(ref invalidatedGraphicsResourceSets, (uint)d3dPipeline.ResourceLayouts.Length);
            }
            else if (pipeline.IsComputePipeline && computePipeline != pipeline)
            {
                var d3dPipeline = Util.AssertSubtype<Pipeline, D3D11Pipeline>(pipeline);
                computePipeline = d3dPipeline;
                clearSets(computeResourceSets); // Invalidate resource set bindings -- they may be invalid.
                Util.ClearArray(invalidatedComputeResourceSets);

                var computeShader = d3dPipeline.ComputeShader;
                DeviceContext.CSSetShader(computeShader);
                Util.EnsureArrayMinimumSize(ref computeResourceSets, (uint)d3dPipeline.ResourceLayouts.Length);
                Util.EnsureArrayMinimumSize(ref invalidatedComputeResourceSets, (uint)d3dPipeline.ResourceLayouts.Length);
            }
        }

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            var d3d11Buffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(buffer);

            if (vertexBindings[index] != d3d11Buffer.Buffer || vertexOffsets[index] != offset)
            {
                vertexBindingsChanged = true;
                unbindUavBuffer(buffer);
                vertexBindings[index] = d3d11Buffer.Buffer;
                vertexOffsets[index] = (int)offset;
                numVertexBindings = Math.Max(index + 1, numVertexBindings);
            }
        }

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            preDrawCommand();
            // V11: a bufferless draw has no vertex buffer, so the base vertex SPIR-V's gl_VertexIndex includes is vertexStart.
            bindVertexInfoForDraw(baseVertex: (int)vertexStart, baseInstance: instanceStart);

            if (instanceCount == 1 && instanceStart == 0)
                DeviceContext.Draw((int)vertexCount, (int)vertexStart);
            else
                DeviceContext.DrawInstanced((int)vertexCount, (int)instanceCount, (int)vertexStart, (int)instanceStart);
        }

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            preDrawCommand();
            bindVertexInfoForDraw(baseVertex: vertexOffset, baseInstance: instanceStart);

            Debug.Assert(ib != null);
            if (instanceCount == 1 && instanceStart == 0)
                DeviceContext.DrawIndexed((int)indexCount, (int)indexStart, vertexOffset);
            else
                DeviceContext.DrawIndexedInstanced((int)indexCount, (int)instanceCount, (int)indexStart, vertexOffset, (int)instanceStart);
        }

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            // Not a pass start: Vulkan defers the clear to the pass's load-op and opens the pass at the first DRAW (see emulatedRenderPassActive).
            DeviceContext.ClearRenderTargetView(d3D11Framebuffer.RenderTargetViews[index], new Color4(clearColor.R, clearColor.G, clearColor.B, clearColor.A));
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            // Not a pass start (same reason as ClearColorTargetCore).
            DeviceContext.ClearDepthStencilView(d3D11Framebuffer.DepthStencilView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, depth, stencil);
        }

        private protected override unsafe void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            var d3dBuffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(buffer);
            if (sizeInBytes == 0) return;

            bool isDynamic = (buffer.Usage & BufferUsage.Dynamic) == BufferUsage.Dynamic;
            bool isStaging = (buffer.Usage & BufferUsage.Staging) == BufferUsage.Staging;
            bool isUniformBuffer = (buffer.Usage & BufferUsage.UniformBuffer) == BufferUsage.UniformBuffer;
            bool useMap = isDynamic;
            bool updateFullBuffer = bufferOffsetInBytes == 0 && sizeInBytes == buffer.SizeInBytes;
            bool useUpdateSubresource = !isDynamic && !isStaging && (!isUniformBuffer || updateFullBuffer);

            if (useUpdateSubresource)
            {
                Box? subregion = new Box((int)bufferOffsetInBytes, 0, 0, (int)(sizeInBytes + bufferOffsetInBytes), 1, 1);

                if (isUniformBuffer)
                    subregion = null;

                if (bufferOffsetInBytes == 0)
                    DeviceContext.UpdateSubresource(d3dBuffer.Buffer, 0, subregion, source, 0, 0);
                else
                    UpdateSubresource_Workaround(d3dBuffer.Buffer, 0, subregion, source);
            }
            else if (useMap && updateFullBuffer) // Can only update full buffer with WriteDiscard.
            {
                var msb = DeviceContext.Map(
                    d3dBuffer.Buffer,
                    0,
                    D3D11Formats.VdToD3D11MapMode(true, MapMode.Write));
                if (sizeInBytes < 1024)
                    Unsafe.CopyBlock(msb.DataPointer.ToPointer(), source.ToPointer(), sizeInBytes);
                else
                    Buffer.MemoryCopy(source.ToPointer(), msb.DataPointer.ToPointer(), buffer.SizeInBytes, sizeInBytes);
                DeviceContext.Unmap(d3dBuffer.Buffer, 0);
            }
            else
            {
                var staging = getFreeStagingBuffer(sizeInBytes);
                gd.UpdateBufferOutsideCommandList(staging, 0, source, sizeInBytes);
                CopyBuffer(staging, 0, buffer, bufferOffsetInBytes, sizeInBytes);
                submittedStagingBuffers.Add(staging);
            }
        }

        private protected override void GenerateMipmapsCore(Texture texture)
        {
            var fullTexView = texture.GetFullTextureView(gd);
            var d3d11View = Util.AssertSubtype<TextureView, D3D11TextureView>(fullTexView);
            var srv = d3d11View.ShaderResourceView;
            DeviceContext.GenerateMips(srv);
        }

        private protected override void PushDebugGroupCore(string name)
        {
            uda?.BeginEvent(name);
        }

        private protected override void PopDebugGroupCore()
        {
            uda?.EndEvent();
        }

        private protected override void InsertDebugMarkerCore(string name)
        {
            uda?.SetMarker(name);
        }

        private struct BoundTextureInfo
        {
            public int Slot;
            public ShaderStages Stages;
            public uint ResourceSet;
        }

        private struct D3D11BufferRange : IEquatable<D3D11BufferRange>
        {
            public readonly D3D11Buffer Buffer;
            public readonly uint Offset;
            public readonly uint Size;

            public bool IsFullRange => Offset == 0 && Size == Buffer.SizeInBytes;

            public D3D11BufferRange(D3D11Buffer buffer, uint offset, uint size)
            {
                Buffer = buffer;
                Offset = offset;
                Size = size;
            }

            public bool Equals(D3D11BufferRange other)
            {
                return Buffer == other.Buffer && Offset.Equals(other.Offset) && Size.Equals(other.Size);
            }
        }
    }
}
