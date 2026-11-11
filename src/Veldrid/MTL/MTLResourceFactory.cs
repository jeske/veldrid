namespace Veldrid.MTL
{
    internal class MtlResourceFactory : ResourceFactory
    {
        public override GraphicsBackend BackendType => GraphicsBackend.Metal;
        private readonly MtlGraphicsDevice gd;

        public MtlResourceFactory(MtlGraphicsDevice gd)
            : base(gd.Features)
        {
            this.gd = gd;
        }

        public override CommandList CreateCommandList(ref CommandListDescription description)
        {
            return new MtlCommandList(ref description, gd);
        }

        protected override Pipeline CreateComputePipelineCore(ref ComputePipelineDescription description)
        {
            return new MtlPipeline(ref description, gd);
        }

        public override Framebuffer CreateFramebuffer(ref FramebufferDescription description)
        {
            return new MtlFramebuffer(gd, ref description);
        }

        protected override ResourceLayout CreateResourceLayoutCore(ref ResourceLayoutDescription description)
        {
            // V9: Metal flattens (set, binding) to argument-table indices, so a layout WITHOUT the compiled bundle's flat binding map cannot be
            // built. Reached only when the shape's bundle has no map entries for the set - a malformed bundle unless the set has no resources.
            if (description.Elements.Length == 0) return new MtlResourceLayout(ref description, gd, 0, System.Array.Empty<VdShaderBindingEntry>());
            throw new ShaderContractException(description.VerifiedShape?.BundleName ?? "(no bundle)",
            [
                new ShaderContractViolation(ShaderContractVerifier.Rule_V9_FlatBindingMap, $"set {description.VerifiedSetIndex}",
                    $"{description.Elements.Length} resource(s)", "no flat binding map entries for this set",
                    "a Metal resource layout is built from the compiled bundle's flat binding map only; the bundle carries none for this set"),
            ]);
        }

        internal override ResourceLayout CreateResourceLayout(
            ref ResourceLayoutDescription description,
            uint setIndex,
            VdShaderBindingEntry[] bindingEntries)
        {
            // The layout constructor throws V9 per element that has no entry.
            return new MtlResourceLayout(ref description, gd, setIndex, bindingEntries);
        }

        protected override ResourceSet CreateResourceSetCore(ref ResourceSetDescription description)
        {
            ValidationHelpers.ValidateResourceSet(gd, ref description);
            return new MtlResourceSet(ref description, gd);
        }

        public override Fence CreateFence(bool signaled)
        {
            return new MtlFence(signaled);
        }

        private protected override TimestampQueryPool CreateTimestampQueryPoolCore(uint capacity, bool registerForImplicitReset)
        {
            // Features.TimestampQueries is false on Metal (MTLCounterSampleBuffer later - _TASKS/GpuTimestampQueries.md 5.5); the public
            // wrapper already threw under VALIDATE_USAGE. Reached only in a non-validating build.
            throw new VeldridException("GPU timestamp queries are not supported on the Metal backend yet.");
        }

        public override Swapchain CreateSwapchain(ref SwapchainDescription description)
        {
            return new MtlSwapchain(gd, ref description);
        }

        protected override Pipeline CreateGraphicsPipelineCore(ref GraphicsPipelineDescription description)
        {
            return new MtlPipeline(ref description, gd);
        }

        protected override Sampler CreateSamplerCore(ref SamplerDescription description)
        {
            return new MtlSampler(ref description, gd);
        }

        protected override Shader CreateShaderCore(ref ShaderDescription description)
        {
            return new MtlShader(ref description, gd);
        }

        protected override DeviceBuffer CreateBufferCore(ref BufferDescription description)
        {
            return new MtlBuffer(ref description, gd);
        }

        protected override Texture CreateTextureCore(ref TextureDescription description)
        {
            if ((description.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil)
                description.Format = MtlFormats.GetSupportedDepthStencilFormat(description.Format, gd);

            return new MtlTexture(ref description, gd);
        }

        protected override Texture CreateTextureCore(ulong nativeTexture, ref TextureDescription description)
        {
            return new MtlTexture(nativeTexture, ref description);
        }

        protected override TextureView CreateTextureViewCore(ref TextureViewDescription description)
        {
            return new MtlTextureView(ref description, gd);
        }
    }
}
