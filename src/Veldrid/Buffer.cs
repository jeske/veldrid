using System;

namespace Veldrid
{
    /// <summary>
    ///     A device resource used to store arbitrary graphics data in various formats.
    ///     The size of a <see cref="DeviceBuffer" /> is fixed upon creation, and resizing is not possible.
    ///     See <see cref="BufferDescription" />.
    /// </summary>
    public abstract class DeviceBuffer : IDeviceResource, IBindableResource, IMappableResource, IDisposable
    {
        /// <summary>
        ///     The total capacity, in bytes, of the buffer. This value is fixed upon creation.
        /// </summary>
        public abstract uint SizeInBytes { get; }

        /// <summary>
        ///     A bitmask indicating how this instance is permitted to be used.
        /// </summary>
        public abstract BufferUsage Usage { get; }

        /// <summary>
        ///     The <see cref="BufferDescription.StructureByteStride"/> this buffer was created with (0 for non-structured / raw buffers).
        ///     Fixed at creation. V10: a <c>StorageBufferSlot&lt;TElement&gt;</c> binds a buffer only when this equals its verified element stride,
        ///     so the stride the shader was compiled for and the stride the buffer was created with can never silently disagree.
        /// </summary>
        public abstract uint StructureByteStride { get; }

        /// <summary>
        ///     Whether this buffer was created with <see cref="BufferDescription.RawBuffer"/>. Fixed at creation. Only D3D11 distinguishes the
        ///     two: a raw buffer gets <c>R32_TYPELESS</c> RAW SRV/UAVs, a non-raw structured buffer gets STRUCTURED views. SPIRV-Cross compiles
        ///     every GLSL <c>buffer</c> block to an HLSL <c>(RW)ByteAddressBuffer</c>, which is only valid against a RAW view - a structured view
        ///     bound there is undefined D3D11 (observed: dynamic-index loads return zeros while constant-index loads work; AN_Monsters _BUGFIX/70).
        ///     V7f: <c>StorageBufferSlot&lt;TElement&gt;.With</c> refuses a non-raw buffer on D3D11. Other backends ignore the flag and report false.
        /// </summary>
        public virtual bool IsRawBuffer => false;

        /// <summary>
        ///     Whether this buffer was created with <see cref="BufferDescription.AllowUpdateBufferOutsideCommandList" />,
        ///     i.e. whether <see cref="GraphicsDevice.UpdateBufferOutsideCommandList(DeviceBuffer, uint, IntPtr, uint)" />
        ///     is permitted on it. Fixed at creation. See that field for the ordering semantics being acknowledged.
        ///     Stamped by <see cref="ResourceFactory.CreateBuffer(ref BufferDescription)" />; backends never see this flag.
        /// </summary>
        public bool AllowUpdateBufferOutsideCommandList { get; internal set; }

        /// <summary>
        ///     A bool indicating whether this instance has been disposed.
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        ///     A string identifying this instance. Can be used to differentiate between objects in graphics debuggers and other
        ///     tools.
        /// </summary>
        public abstract string Name { get; set; }

        #region Disposal

        /// <summary>
        ///     Frees unmanaged device resources controlled by this instance.
        /// </summary>
        public abstract void Dispose();

        #endregion
    }
}
