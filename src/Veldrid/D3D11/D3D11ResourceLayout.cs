namespace Veldrid.D3D11
{
    internal class D3D11ResourceLayout : ResourceLayout
    {
        public int UniformBufferCount { get; }
        public int StorageBufferCount { get; }
        public int TextureCount { get; }
        public int SamplerCount { get; }

        /// <summary>
        /// When true, this layout has explicit binding slot assignments from the shader compiler.
        /// The slot values are absolute global D3D11 registers (bN, tN, sN, uN) and must NOT be
        /// offset by cbBase/textureBase/samplerBase/uaBase at draw time.
        /// This is true when created from a .vdshader bundle's flat binding map.
        /// </summary>
        public bool HasExplicitBindingSlots { get; }

        public override bool IsDisposed => disposed;

        public override string Name { get; set; }

        private readonly ResourceBindingInfo[] bindingInfosByVdIndex;
        private bool disposed;

        /// <summary>
        /// Standard constructor: computes sequential slot indices (legacy Veldrid behavior).
        /// Used when no binding map is available.
        /// </summary>
        public D3D11ResourceLayout(ref ResourceLayoutDescription description)
            : base(ref description)
        {
            var elements = description.Elements;
            bindingInfosByVdIndex = new ResourceBindingInfo[elements.Length];

            int cbIndex = 0;
            int texIndex = 0;
            int samplerIndex = 0;
            int unorderedAccessIndex = 0;

            for (int i = 0; i < bindingInfosByVdIndex.Length; i++)
            {
                int slot;

                switch (elements[i].Kind)
                {
                    case ResourceKind.UniformBuffer:
                        slot = cbIndex++;
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                        slot = texIndex++;
                        break;

                    case ResourceKind.StructuredBufferReadWrite:
                        slot = unorderedAccessIndex++;
                        break;

                    case ResourceKind.TextureReadOnly:
                        slot = texIndex++;
                        break;

                    case ResourceKind.TextureReadWrite:
                        slot = unorderedAccessIndex++;
                        break;

                    case ResourceKind.Sampler:
                        slot = samplerIndex++;
                        break;

                    default: throw Illegal.Value<ResourceKind>();
                }

                bindingInfosByVdIndex[i] = new ResourceBindingInfo(
                    slot,
                    elements[i].Stages,
                    elements[i].Kind,
                    (elements[i].Options & ResourceLayoutElementOptions.DynamicBinding) != 0);
            }

            UniformBufferCount = cbIndex;
            StorageBufferCount = unorderedAccessIndex;
            TextureCount = texIndex;
            SamplerCount = samplerIndex;
        }

        /// <summary>
        /// Compiler-aligned constructor: uses the flat binding map from the shader compiler as the source of truth.
        /// Slot assignments come directly from <see cref="VdShaderBindingEntry.FlatIndex"/> — the exact
        /// D3D11 register indices (bN, tN, sN, uN) baked into the compiled shader.
        /// Used for precompiled bundles that provide a binding map.
        /// </summary>
        public D3D11ResourceLayout(
            ref ResourceLayoutDescription description,
            uint setIndex,
            VdShaderBindingEntry[] bindingEntries)
            : base(ref description)
        {
            var elements = description.Elements;
            bindingInfosByVdIndex = new ResourceBindingInfo[elements.Length];
            HasExplicitBindingSlots = true;

            int cbCount = 0;
            int texCount = 0;
            int samplerCount = 0;
            int uaCount = 0;

            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];

                // Find matching binding entry from the flat binding map.
                // D3D11 uses global flat registers, so vertex and fragment share the same register space.
                // Match by set + binding index (binding = element index within the set).
                int slot = -1;
                foreach (var entry in bindingEntries)
                {
                    if (entry.Set == setIndex && entry.Binding == (uint)i)
                    {
                        slot = (int)entry.FlatIndex;
                        break;
                    }
                }

                if (slot < 0)
                {
                    // Fallback: no binding entry found for this element.
                    // This shouldn't happen with a well-formed bundle, but handle gracefully
                    // by using sequential assignment.
                    slot = element.Kind switch
                    {
                        ResourceKind.UniformBuffer => cbCount,
                        ResourceKind.StructuredBufferReadOnly => texCount,
                        ResourceKind.StructuredBufferReadWrite => uaCount,
                        ResourceKind.TextureReadOnly => texCount,
                        ResourceKind.TextureReadWrite => uaCount,
                        ResourceKind.Sampler => samplerCount,
                        _ => throw Illegal.Value<ResourceKind>()
                    };
                }

                // Track counts (still needed for compatibility / validation)
                switch (element.Kind)
                {
                    case ResourceKind.UniformBuffer: cbCount++; break;
                    case ResourceKind.StructuredBufferReadOnly: texCount++; break;
                    case ResourceKind.StructuredBufferReadWrite: uaCount++; break;
                    case ResourceKind.TextureReadOnly: texCount++; break;
                    case ResourceKind.TextureReadWrite: uaCount++; break;
                    case ResourceKind.Sampler: samplerCount++; break;
                }

                bindingInfosByVdIndex[i] = new ResourceBindingInfo(
                    slot,
                    element.Stages,
                    element.Kind,
                    (element.Options & ResourceLayoutElementOptions.DynamicBinding) != 0);
            }

            UniformBufferCount = cbCount;
            StorageBufferCount = uaCount;
            TextureCount = texCount;
            SamplerCount = samplerCount;
        }

        #region Disposal

        public override void Dispose()
        {
            disposed = true;
        }

        #endregion

        public ResourceBindingInfo GetDeviceSlotIndex(int resourceLayoutIndex)
        {
            if (resourceLayoutIndex >= bindingInfosByVdIndex.Length) throw new VeldridException($"Invalid resource index: {resourceLayoutIndex}. Maximum is: {bindingInfosByVdIndex.Length - 1}.");

            return bindingInfosByVdIndex[resourceLayoutIndex];
        }

        public bool IsDynamicBuffer(int index)
        {
            return bindingInfosByVdIndex[index].DynamicBuffer;
        }

        internal struct ResourceBindingInfo
        {
            public int Slot;
            public ShaderStages Stages;
            public ResourceKind Kind;
            public bool DynamicBuffer;

            public ResourceBindingInfo(int slot, ShaderStages stages, ResourceKind kind, bool dynamicBuffer)
            {
                Slot = slot;
                Stages = stages;
                Kind = kind;
                DynamicBuffer = dynamicBuffer;
            }
        }
    }
}