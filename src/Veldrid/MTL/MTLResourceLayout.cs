namespace Veldrid.MTL
{
    internal class MtlResourceLayout : ResourceLayout
    {
        private readonly ResourceBindingInfo[] bindingInfosByVdIndex;
        private bool disposed;
        public uint BufferCount { get; }
        public uint TextureCount { get; }
        public uint SamplerCount { get; }

        /// <summary>
        /// When true, this layout has explicit binding slot assignments from the shader compiler.
        /// The slot values are absolute (the compiler is the source of truth) and must NOT be offset
        /// by baseBuffer/baseTexture/baseSampler at draw time.
        /// This is true for both precompiled bundles and runtime-compiled shaders that provide a binding map.
        /// </summary>
        public bool HasExplicitBindingSlots { get; }

#if !VALIDATE_USAGE
        public ResourceKind[] ResourceKinds { get; }
#endif
        public ResourceBindingInfo GetBindingInfo(int index)
        {
            return bindingInfosByVdIndex[index];
        }

#if !VALIDATE_USAGE
        public ResourceLayoutDescription Description { get; }
#endif

        /// <summary>
        /// Standard constructor: computes sequential slot indices (legacy Veldrid behavior).
        /// Used when no binding map is available.
        /// </summary>
        public MtlResourceLayout(ref ResourceLayoutDescription description, MtlGraphicsDevice gd)
            : base(ref description)
        {
#if !VALIDATE_USAGE
            Description = description;
#endif

            var elements = description.Elements;
#if !VALIDATE_USAGE
            ResourceKinds = new ResourceKind[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                ResourceKinds[i] = elements[i].Kind;
            }
#endif

            bindingInfosByVdIndex = new ResourceBindingInfo[elements.Length];
            HasExplicitBindingSlots = false;

            uint bufferIndex = 0;
            uint texIndex = 0;
            uint samplerIndex = 0;

            for (int i = 0; i < bindingInfosByVdIndex.Length; i++)
            {
                uint slot;

                switch (elements[i].Kind)
                {
                    case ResourceKind.UniformBuffer:
                        slot = bufferIndex++;
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                        slot = bufferIndex++;
                        break;

                    case ResourceKind.StructuredBufferReadWrite:
                        slot = bufferIndex++;
                        break;

                    case ResourceKind.TextureReadOnly:
                        slot = texIndex++;
                        break;

                    case ResourceKind.TextureReadWrite:
                        slot = texIndex++;
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

            BufferCount = bufferIndex;
            TextureCount = texIndex;
            SamplerCount = samplerIndex;
        }

        /// <summary>
        /// Compiler-aligned constructor: uses the flat binding map from the shader compiler as the source of truth.
        /// Slot assignments come directly from <see cref="VdShaderBindingEntry.FlatIndex"/> — the exact
        /// Metal buffer(N)/texture(N)/sampler(N) indices baked into the compiled shader.
        /// Used for both precompiled bundles and runtime-compiled shaders that provide a binding map.
        /// </summary>
        public MtlResourceLayout(
            ref ResourceLayoutDescription description,
            MtlGraphicsDevice gd,
            uint setIndex,
            VdShaderBindingEntry[] bindingEntries)
            : base(ref description)
        {
#if !VALIDATE_USAGE
            Description = description;
#endif

            var elements = description.Elements;
#if !VALIDATE_USAGE
            ResourceKinds = new ResourceKind[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                ResourceKinds[i] = elements[i].Kind;
            }
#endif

            bindingInfosByVdIndex = new ResourceBindingInfo[elements.Length];
            HasExplicitBindingSlots = true;

            uint bufferCount = 0;
            uint texCount = 0;
            uint samplerCount = 0;

            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];

                // Find matching binding entries from the flat binding map.
                // The binding map may have SEPARATE entries per stage (vertex vs fragment)
                // because Metal has separate argument tables per shader stage.
                // Match by set + binding index (binding = element index within the set).
                uint vertexSlot = 0;
                uint fragmentSlot = 0;
                bool foundVertex = false;
                bool foundFragment = false;

                foreach (var entry in bindingEntries)
                {
                    if (entry.Set != setIndex || entry.Binding != (uint)i)
                        continue;

                    if ((entry.Stages & ShaderStages.Vertex) != 0)
                    {
                        vertexSlot = entry.FlatIndex;
                        foundVertex = true;
                    }
                    if ((entry.Stages & ShaderStages.Fragment) != 0)
                    {
                        fragmentSlot = entry.FlatIndex;
                        foundFragment = true;
                    }
                }

                // If only one stage was found, use its slot for both (common case: resource only in one stage)
                if (foundVertex && !foundFragment)
                    fragmentSlot = vertexSlot;
                else if (foundFragment && !foundVertex)
                    vertexSlot = fragmentSlot;
                else if (!foundVertex && !foundFragment)
                {
                    // Fallback: no binding entry found for this element.
                    // This shouldn't happen with a well-formed bundle, but handle gracefully.
                    uint fallbackSlot = element.Kind switch
                    {
                        ResourceKind.UniformBuffer or ResourceKind.StructuredBufferReadOnly
                            or ResourceKind.StructuredBufferReadWrite => bufferCount,
                        ResourceKind.TextureReadOnly or ResourceKind.TextureReadWrite => texCount,
                        ResourceKind.Sampler => samplerCount,
                        _ => throw Illegal.Value<ResourceKind>()
                    };
                    vertexSlot = fallbackSlot;
                    fragmentSlot = fallbackSlot;
                }

                // Track counts for compatibility (used by pipeline for NonVertexBufferCount etc.)
                switch (element.Kind)
                {
                    case ResourceKind.UniformBuffer:
                    case ResourceKind.StructuredBufferReadOnly:
                    case ResourceKind.StructuredBufferReadWrite:
                        bufferCount++;
                        break;
                    case ResourceKind.TextureReadOnly:
                    case ResourceKind.TextureReadWrite:
                        texCount++;
                        break;
                    case ResourceKind.Sampler:
                        samplerCount++;
                        break;
                }

                bindingInfosByVdIndex[i] = new ResourceBindingInfo(
                    vertexSlot,
                    fragmentSlot,
                    element.Stages,
                    element.Kind,
                    (element.Options & ResourceLayoutElementOptions.DynamicBinding) != 0);
            }

            BufferCount = bufferCount;
            TextureCount = texCount;
            SamplerCount = samplerCount;
        }

        public override string Name { get; set; }

        public override bool IsDisposed => disposed;

        public override void Dispose()
        {
            disposed = true;
        }

        internal struct ResourceBindingInfo
        {
            /// <summary>
            /// The slot index for this resource. In legacy/Improved mode, this is the same for all stages.
            /// In ShaderBundleControlled mode, this is the vertex-stage slot (use FragmentSlot for fragment).
            /// </summary>
            public uint Slot;

            /// <summary>
            /// The fragment-stage slot index. Only different from Slot in ShaderBundleControlled mode
            /// when the shader compiler assigns different buffer indices per stage.
            /// In legacy mode, this equals Slot.
            /// </summary>
            public uint FragmentSlot;

            public ShaderStages Stages;
            public ResourceKind Kind;
            public bool DynamicBuffer;

            public ResourceBindingInfo(uint slot, ShaderStages stages, ResourceKind kind, bool dynamicBuffer)
            {
                Slot = slot;
                FragmentSlot = slot;
                Stages = stages;
                Kind = kind;
                DynamicBuffer = dynamicBuffer;
            }

            public ResourceBindingInfo(uint vertexSlot, uint fragmentSlot, ShaderStages stages, ResourceKind kind, bool dynamicBuffer)
            {
                Slot = vertexSlot;
                FragmentSlot = fragmentSlot;
                Stages = stages;
                Kind = kind;
                DynamicBuffer = dynamicBuffer;
            }
        }
    }
}
