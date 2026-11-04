namespace Veldrid.D3D11
{
    /// <summary>
    /// D3D11 registers for one resource layout. There is ONE way to build it (V9): every element's register (bN / tN / sN / uN) is the
    /// <see cref="VdShaderBindingEntry.FlatIndex"/> the shader compiler assigned and baked into the compiled HLSL - absolute, global,
    /// never offset at draw time. An element with no entry is a contract violation, not a counted guess.
    /// </summary>
    internal class D3D11ResourceLayout : ResourceLayout
    {
        public override bool IsDisposed => disposed;

        public override string Name { get; set; }

        private readonly ResourceBindingInfo[] bindingInfosByVdIndex;
        private bool disposed;

        /// <summary>Compiler-aligned constructor (the only one): registers from the bundle's flat binding map for set <paramref name="setIndex"/>.</summary>
        /// <exception cref="ShaderContractException">V9: an element of the set has no binding-map entry.</exception>
        public D3D11ResourceLayout(
            ref ResourceLayoutDescription description,
            uint setIndex,
            VdShaderBindingEntry[] bindingEntries)
            : base(ref description)
        {
            var elements = description.Elements;
            bindingInfosByVdIndex = new ResourceBindingInfo[elements.Length];
            bindingEntries ??= System.Array.Empty<VdShaderBindingEntry>();

            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];

                // D3D11 registers are global per kind (vertex and fragment share one register space); the map is keyed by GLSL (set, binding),
                // binding = element index within the set.
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
                    throw new ShaderContractException(description.VerifiedShape?.BundleName ?? "(no bundle)",
                    [
                        new ShaderContractViolation(ShaderContractVerifier.Rule_V9_FlatBindingMap, $"set {setIndex} binding {i} ({element.Name})",
                            $"{element.Kind}", "no flat binding map entry",
                            "the D3D11 register of every resource comes from the compiled bundle's flat binding map; nothing is counted or guessed"),
                    ]);
                }

                bindingInfosByVdIndex[i] = new ResourceBindingInfo(
                    slot,
                    element.Stages,
                    element.Kind,
                    (element.Options & ResourceLayoutElementOptions.DynamicBinding) != 0);
            }
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