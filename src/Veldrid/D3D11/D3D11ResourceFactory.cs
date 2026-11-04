using System;
using Vortice.Direct3D11;

namespace Veldrid.D3D11
{
    internal class D3D11ResourceFactory : ResourceFactory, IDisposable
    {
        public override GraphicsBackend BackendType => GraphicsBackend.Direct3D11;
        private readonly D3D11GraphicsDevice gd;
        private readonly ID3D11Device device;
        private readonly D3D11ResourceCache cache;

        public D3D11ResourceFactory(D3D11GraphicsDevice gd)
            : base(gd.Features)
        {
            this.gd = gd;
            device = gd.Device;
            cache = new D3D11ResourceCache(device);
        }

        #region Disposal

        public void Dispose()
        {
            cache.Dispose();
        }

        #endregion

        public override CommandList CreateCommandList(ref CommandListDescription description)
        {
            return new D3D11CommandList(gd, ref description);
        }

        public override Framebuffer CreateFramebuffer(ref FramebufferDescription description)
        {
            return new D3D11Framebuffer(device, ref description);
        }

        protected override Pipeline CreateComputePipelineCore(ref ComputePipelineDescription description)
        {
            return new D3D11Pipeline(cache, ref description);
        }

        protected override ResourceLayout CreateResourceLayoutCore(ref ResourceLayoutDescription description)
        {
            // V9: D3D11 flattens (set, binding) to absolute registers, so a layout WITHOUT the compiled bundle's flat binding map cannot be
            // built - there is nothing to count from. ResourceFactory.CreateResourceLayout reaches this only when the shape's bundle has no
            // map entries for the set, which on D3D11 means a malformed bundle (or a set declared with no resources that still got a layout).
            if (description.Elements.Length == 0) return new D3D11ResourceLayout(ref description, 0, System.Array.Empty<VdShaderBindingEntry>());
            throw new ShaderContractException(description.VerifiedShape?.BundleName ?? "(no bundle)",
            [
                new ShaderContractViolation(ShaderContractVerifier.Rule_V9_FlatBindingMap, $"set {description.VerifiedSetIndex}",
                    $"{description.Elements.Length} resource(s)", "no flat binding map entries for this set",
                    "a D3D11 resource layout is built from the compiled bundle's flat binding map only; the bundle carries none for this set"),
            ]);
        }

        internal override ResourceLayout CreateResourceLayout(
            ref ResourceLayoutDescription description,
            uint setIndex,
            VdShaderBindingEntry[] bindingEntries)
        {
            // The layout constructor throws V9 per element that has no entry; an empty map with resources fails on its first element.
            return new D3D11ResourceLayout(ref description, setIndex, bindingEntries);
        }

        protected override ResourceSet CreateResourceSetCore(ref ResourceSetDescription description)
        {
            ValidationHelpers.ValidateResourceSet(gd, ref description);
            return new D3D11ResourceSet(ref description);
        }

        public override Fence CreateFence(bool signaled)
        {
            return new D3D11Fence(signaled);
        }

        public override Swapchain CreateSwapchain(ref SwapchainDescription description)
        {
            return new D3D11Swapchain(gd, ref description);
        }

        protected override Pipeline CreateGraphicsPipelineCore(ref GraphicsPipelineDescription description)
        {
            return new D3D11Pipeline(cache, ref description);
        }

        protected override Sampler CreateSamplerCore(ref SamplerDescription description)
        {
            return new D3D11Sampler(device, ref description);
        }

        protected override Shader CreateShaderCore(ref ShaderDescription description)
        {
            return new D3D11Shader(device, description);
        }

        protected override Texture CreateTextureCore(ref TextureDescription description)
        {
            return new D3D11Texture(device, ref description);
        }

        protected override Texture CreateTextureCore(ulong nativeTexture, ref TextureDescription description)
        {
            var existingTexture = new ID3D11Texture2D((IntPtr)nativeTexture);
            return new D3D11Texture(existingTexture, description.Type, description.Format);
        }

        protected override TextureView CreateTextureViewCore(ref TextureViewDescription description)
        {
            return new D3D11TextureView(gd, ref description);
        }

        protected override DeviceBuffer CreateBufferCore(ref BufferDescription description)
        {
            return new D3D11Buffer(
                device,
                description.SizeInBytes,
                description.Usage,
                description.StructureByteStride,
                description.RawBuffer);
        }
    }
}
