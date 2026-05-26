using System;
using System.Collections.Generic;

namespace Veldrid
{
    /// <summary>
    /// The result of loading a precompiled shader bundle via
    /// <see cref="ResourceFactory.CreateFromBundle(VeldridShaderBundle, Func{string, byte[]}, string)"/>.
    /// Contains the created <see cref="Shader"/> objects, the <see cref="ResourceLayoutDescription"/> array
    /// that MUST be used to create <see cref="ResourceLayout"/> objects, and the flat binding map
    /// that backends use to bind resources to the correct slots.
    /// </summary>
    public sealed class PrecompiledShaderResult
    {
        /// <summary>
        /// The compiled shader objects (vertex and fragment, in that order).
        /// </summary>
        public Shader[] Shaders { get; }

        /// <summary>
        /// The resource layout descriptions from the shader bundle.
        /// These MUST be used to create <see cref="ResourceLayout"/> objects — they guarantee
        /// correct resource binding on every backend (Vulkan, D3D11, Metal, OpenGL).
        /// Array index corresponds to the descriptor set number.
        /// </summary>
        public ResourceLayoutDescription[] ResourceLayouts { get; }

        /// <summary>
        /// The flat binding map from the cross-compiler. Records the exact Metal buffer/texture/sampler
        /// slot (or D3D11 register) that each resource occupies in the compiled shader.
        /// Backends use this to bind resources to the correct slots instead of computing sequential indices.
        /// May be null if the bundle does not include a binding map.
        /// </summary>
        public VdShaderBindingEntry[] FlatBindingMap { get; }

        /// <summary>
        /// Creates a new <see cref="PrecompiledShaderResult"/>.
        /// </summary>
        /// <param name="shaders">The compiled shader objects.</param>
        /// <param name="resourceLayouts">The resource layout descriptions from the bundle.</param>
        /// <param name="flatBindingMap">The flat binding map from the cross-compiler (may be null).</param>
        public PrecompiledShaderResult(
            Shader[] shaders,
            ResourceLayoutDescription[] resourceLayouts,
            VdShaderBindingEntry[] flatBindingMap = null)
        {
            Shaders = shaders;
            ResourceLayouts = resourceLayouts;
            FlatBindingMap = flatBindingMap;
        }

        /// <summary>
        /// Creates all <see cref="ResourceLayout"/> objects from this bundle's layout descriptions,
        /// using the flat binding map to ensure correct slot assignments on all backends.
        /// This is the recommended way to create ResourceLayouts from a precompiled shader bundle.
        /// </summary>
        /// <param name="factory">The resource factory to create layouts with.</param>
        /// <returns>
        /// An array of <see cref="ResourceLayout"/> objects, one per descriptor set,
        /// with slot assignments matching the compiled shader.
        /// </returns>
        public ResourceLayout[] CreateResourceLayouts(ResourceFactory factory)
        {
            if (ResourceLayouts == null || ResourceLayouts.Length == 0)
                return [];

            var layouts = new ResourceLayout[ResourceLayouts.Length];

            for (uint i = 0; i < ResourceLayouts.Length; i++)
            {
                var desc = ResourceLayouts[i];

                if (FlatBindingMap != null && FlatBindingMap.Length > 0)
                {
                    // Filter binding entries for this set
                    var setEntries = new List<VdShaderBindingEntry>();
                    foreach (var entry in FlatBindingMap)
                    {
                        if (entry.Set == i)
                            setEntries.Add(entry);
                    }
                    layouts[i] = factory.CreateResourceLayout(ref desc, i, setEntries.ToArray());
                }
                else
                {
                    layouts[i] = factory.CreateResourceLayout(ref desc);
                }
            }

            return layouts;
        }
    }
}
