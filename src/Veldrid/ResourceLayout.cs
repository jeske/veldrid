using System;

namespace Veldrid
{
    /// <summary>
    ///     A device resource which describes the layout and kind of <see cref="IBindableResource" /> objects available
    ///     to a shader set.
    ///     See <see cref="ResourceLayoutDescription" />.
    /// </summary>
    public abstract class ResourceLayout : IDeviceResource, IDisposable
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

        /// <summary>The shape whose slots built this layout (null for Veldrid-internal raw layouts) and its descriptor-set index.</summary>
        internal ShaderPipelineShape VerifiedShape { get; }
        internal uint VerifiedSetIndex { get; }
        /// <summary>Offset-selectable bindings of this layout in binding order (count = dynamic offsets expected at bind time).</summary>
        internal int VerifiedOffsetSelectableCount { get; }

        internal ResourceLayout(ref ResourceLayoutDescription description)
        {
            VerifiedShape = description.VerifiedShape;
            VerifiedSetIndex = description.VerifiedSetIndex;
            foreach (var element in description.Elements ?? Array.Empty<ResourceLayoutElementDescription>())
                if ((element.Options & ResourceLayoutElementOptions.DynamicBinding) != 0) VerifiedOffsetSelectableCount++;
#if VALIDATE_USAGE
            Description = description;

            foreach (var element in description.Elements)
            {
                if ((element.Options & ResourceLayoutElementOptions.DynamicBinding) != 0) DynamicBufferCount += 1;
            }
#endif
        }

        #region Disposal

        /// <summary>
        ///     Frees unmanaged device resources controlled by this instance.
        /// </summary>
        public abstract void Dispose();

        #endregion

#if VALIDATE_USAGE
        internal readonly ResourceLayoutDescription Description;
        internal readonly uint DynamicBufferCount;
#endif
    }
}
