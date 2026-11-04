namespace Veldrid.MTL
{
    /// <summary>
    /// Metal argument-table indices for one resource layout. ONE way to build it (V9): every element's buffer(N)/texture(N)/sampler(N)
    /// index is the <see cref="VdShaderBindingEntry.FlatIndex"/> the shader compiler assigned per stage - absolute, never offset at draw time.
    /// </summary>
    internal class MtlResourceLayout : ResourceLayout
    {
        private readonly ResourceBindingInfo[] bindingInfosByVdIndex;
        private bool disposed;
        public uint BufferCount { get; }
        public uint TextureCount { get; }
        public uint SamplerCount { get; }

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

        /// <summary>Compiler-aligned constructor (the only one, V9): Metal buffer(N)/texture(N)/sampler(N) indices from the bundle's flat binding map.</summary>
        /// <exception cref="ShaderContractException">V9: an element of the set has no binding-map entry.</exception>
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
            bindingEntries ??= System.Array.Empty<VdShaderBindingEntry>();

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

                // If only one stage was found, use its slot for both (common case: resource only in one stage).
                if (foundVertex && !foundFragment)
                    fragmentSlot = vertexSlot;
                else if (foundFragment && !foundVertex)
                    vertexSlot = fragmentSlot;
                else if (!foundVertex && !foundFragment)
                {
                    // V9: no entry = the compiler never placed this resource; nothing may be counted or guessed.
                    throw new ShaderContractException(description.VerifiedShape?.BundleName ?? "(no bundle)",
                    [
                        new ShaderContractViolation(ShaderContractVerifier.Rule_V9_FlatBindingMap, $"set {setIndex} binding {i} ({element.Name})",
                            $"{element.Kind}", "no flat binding map entry",
                            "the Metal argument-table index of every resource comes from the compiled bundle's flat binding map; nothing is counted or guessed"),
                    ]);
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
