using System;

namespace Veldrid
{
    /// <summary>
    ///     Describes a <see cref="DeviceBuffer" />, used in the creation of <see cref="DeviceBuffer" /> objects by a
    ///     <see cref="ResourceFactory" />.
    /// </summary>
    public struct BufferDescription : IEquatable<BufferDescription>
    {
        /// <summary>
        ///     The desired capacity, in bytes, of the <see cref="DeviceBuffer" />.
        /// </summary>
        public uint SizeInBytes;

        /// <summary>
        ///     Indicates how the <see cref="DeviceBuffer" /> will be used.
        /// </summary>
        public BufferUsage Usage;

        /// <summary>
        ///     For structured buffers, this value indicates the size in bytes of a single structure element, and must be non-zero.
        ///     For all other buffer types, this value must be zero.
        /// </summary>
        public uint StructureByteStride;

        /// <summary>
        ///     Indicates that this is a raw buffer. This should be combined with
        ///     <see cref="BufferUsage.StructuredBufferReadWrite" />. This affects how the buffer is bound in the D3D11 backend.
        /// </summary>
        public bool RawBuffer;

        /// <summary>
        ///     Explicit acknowledgement that the created <see cref="DeviceBuffer" /> may be written with
        ///     <see cref="GraphicsDevice.UpdateBufferOutsideCommandList(DeviceBuffer, uint, IntPtr, uint)" />.
        ///     Default false: without it that method throws <see cref="VeldridException" />.
        ///     <para>
        ///     A device-level update is an IMMEDIATE host write with no position in any <see cref="CommandList" />.
        ///     Every not-yet-submitted command list, including commands recorded BEFORE the call, observes the new bytes;
        ///     a still-executing submission races it. "Once per frame" is NOT a safety property: the previous frame's
        ///     submission may still be reading the buffer. The write is legal only when NOTHING in flight or already recorded
        ///     references the target memory - e.g. a staging buffer or ring slot being prepared for a FUTURE submission -
        ///     and the caller guarantees quiescence (fence / WaitForIdle / slot discipline) before it is consumed.
        ///     Setting this flag states that the caller understands and owns that condition.
        ///     </para>
        ///     <para>
        ///     This flag does NOT change memory class or native allocation. Writes that must be ordered between recorded
        ///     commands use <see cref="CommandList.UpdateBuffer(DeviceBuffer, uint, IntPtr, uint)" />, which never needs it.
        ///     </para>
        /// </summary>
        public bool AllowUpdateBufferOutsideCommandList;

        /// <summary>
        ///     Constructs a new <see cref="BufferDescription" /> describing a non-dynamic <see cref="DeviceBuffer" />.
        /// </summary>
        /// <param name="sizeInBytes">The desired capacity, in bytes.</param>
        /// <param name="usage">Indicates how the <see cref="DeviceBuffer" /> will be used.</param>
        public BufferDescription(uint sizeInBytes, BufferUsage usage)
        {
            SizeInBytes = sizeInBytes;
            Usage = usage;
            StructureByteStride = 0;
            RawBuffer = false;
            AllowUpdateBufferOutsideCommandList = false;
        }

        /// <summary>
        ///     Constructs a new <see cref="BufferDescription" />.
        /// </summary>
        /// <param name="sizeInBytes">The desired capacity, in bytes.</param>
        /// <param name="usage">Indicates how the <see cref="DeviceBuffer" /> will be used.</param>
        /// <param name="structureByteStride">
        ///     For structured buffers, this value indicates the size in bytes of a single
        ///     structure element, and must be non-zero. For all other buffer types, this value must be zero.
        /// </param>
        public BufferDescription(uint sizeInBytes, BufferUsage usage, uint structureByteStride)
        {
            SizeInBytes = sizeInBytes;
            Usage = usage;
            StructureByteStride = structureByteStride;
            RawBuffer = false;
            AllowUpdateBufferOutsideCommandList = false;
        }

        /// <summary>
        ///     Constructs a new <see cref="BufferDescription" />.
        /// </summary>
        /// <param name="sizeInBytes">The desired capacity, in bytes.</param>
        /// <param name="usage">Indicates how the <see cref="DeviceBuffer" /> will be used.</param>
        /// <param name="structureByteStride">
        ///     For structured buffers, this value indicates the size in bytes of a single
        ///     structure element, and must be non-zero. For all other buffer types, this value must be zero.
        /// </param>
        /// <param name="rawBuffer">
        ///     Indicates that this is a raw buffer. This should be combined with
        ///     <see cref="BufferUsage.StructuredBufferReadWrite" />. This affects how the buffer is bound in the D3D11 backend.
        /// </param>
        public BufferDescription(uint sizeInBytes, BufferUsage usage, uint structureByteStride, bool rawBuffer)
        {
            SizeInBytes = sizeInBytes;
            Usage = usage;
            StructureByteStride = structureByteStride;
            RawBuffer = rawBuffer;
            AllowUpdateBufferOutsideCommandList = false;
        }

        /// <summary>
        ///     Constructs a new <see cref="BufferDescription" />, including the explicit
        ///     <see cref="AllowUpdateBufferOutsideCommandList" /> acknowledgement.
        /// </summary>
        /// <param name="sizeInBytes">The desired capacity, in bytes.</param>
        /// <param name="usage">Indicates how the <see cref="DeviceBuffer" /> will be used.</param>
        /// <param name="structureByteStride">
        ///     For structured buffers, this value indicates the size in bytes of a single
        ///     structure element, and must be non-zero. For all other buffer types, this value must be zero.
        /// </param>
        /// <param name="rawBuffer">
        ///     Indicates that this is a raw buffer. This should be combined with
        ///     <see cref="BufferUsage.StructuredBufferReadWrite" />. This affects how the buffer is bound in the D3D11 backend.
        /// </param>
        /// <param name="allowUpdateBufferOutsideCommandList">
        ///     See <see cref="AllowUpdateBufferOutsideCommandList" />. True permits
        ///     <see cref="GraphicsDevice.UpdateBufferOutsideCommandList(DeviceBuffer, uint, IntPtr, uint)" /> on the created buffer.
        /// </param>
        public BufferDescription(
            uint sizeInBytes,
            BufferUsage usage,
            uint structureByteStride,
            bool rawBuffer,
            bool allowUpdateBufferOutsideCommandList)
        {
            SizeInBytes = sizeInBytes;
            Usage = usage;
            StructureByteStride = structureByteStride;
            RawBuffer = rawBuffer;
            AllowUpdateBufferOutsideCommandList = allowUpdateBufferOutsideCommandList;
        }

        /// <summary>
        ///     Element-wise equality.
        /// </summary>
        /// <param name="other">The instance to compare to.</param>
        /// <returns>True if all elements are equal; false otherswise.</returns>
        public bool Equals(BufferDescription other)
        {
            return SizeInBytes.Equals(other.SizeInBytes)
                   && Usage == other.Usage
                   && StructureByteStride.Equals(other.StructureByteStride)
                   && RawBuffer.Equals(other.RawBuffer)
                   && AllowUpdateBufferOutsideCommandList == other.AllowUpdateBufferOutsideCommandList;
        }

        /// <summary>
        ///     Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
        public override int GetHashCode()
        {
            return HashHelper.Combine(
                SizeInBytes.GetHashCode(),
                (int)Usage,
                StructureByteStride.GetHashCode(),
                RawBuffer.GetHashCode(),
                AllowUpdateBufferOutsideCommandList.GetHashCode());
        }
    }
}
