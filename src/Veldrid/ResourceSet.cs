using System;

namespace Veldrid
{
    /// <summary>
    ///     A device resource used to bind a particular set of <see cref="IBindableResource" /> objects to a
    ///     <see cref="CommandList" />.
    ///     See <see cref="ResourceSetDescription" />.
    /// </summary>
    public abstract class ResourceSet : IDeviceResource, IDisposable
    {
        /// <summary>
        ///     A bool indicating whether this instance has been disposed.
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        ///     A string identifying this instance. Can be used to differentiate between objects in graphics debuggers and other
        ///     tools.
        /// </summary>
        public abstract string Name { get; set; }

        internal ResourceSet(ref ResourceSetDescription description)
        {
#if VALIDATE_USAGE
            Layout = description.Layout;
            Resources = description.BoundResources;
#endif
        }

        #region Disposal

        /// <summary>
        ///     Frees unmanaged device resources controlled by this instance.
        /// </summary>
        public abstract void Dispose();

        #endregion

        // Set when created through ResourceFactory.CreateResourceSet(ShaderPipelineShape, ...): the shape whose layout this set
        // instantiates, its descriptor-set index (replaces the uint in CommandList.SetGraphicsResourceSet), and - for every
        // [UniformBufferBindingTimeOffsetSelectable] binding in binding order - the arena that turns a UniformArenaElement into a byte offset.
        internal ShaderPipelineShape VerifiedShape { get; set; }
        internal uint VerifiedSetIndex { get; set; }
        internal IUniformArenaOffsets[] VerifiedOffsetArenas { get; set; } = Array.Empty<IUniformArenaOffsets>();

#if VALIDATE_USAGE
        internal ResourceLayout Layout { get; }
        internal IBindableResource[] Resources { get; }
#endif
    }
}
