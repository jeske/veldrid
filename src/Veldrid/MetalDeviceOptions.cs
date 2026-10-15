// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace Veldrid
{
    /// <summary>
    ///     A structure describing Metal-specific device creation options.
    /// </summary>
    public struct MetalDeviceOptions
    {
        /// <summary>
        ///     Indicates whether the depth/stencil attachments of a framebuffer are preferred not to be stored in system memory.
        ///     This only affects Apple GPUs.
        /// </summary>
        public bool PreferMemorylessDepthTargets;

        /// <summary>
        ///     Cap on command buffers alive at once on the device's MTLCommandQueue (created and not yet completed; uncommitted ones
        ///     count until released). 0 = Metal's own default (64). Reaching the cap would make -[MTLCommandQueue commandBuffer] block
        ///     forever, so Veldrid tracks the count itself and <see cref="CommandList.Begin"/> throws a <see cref="VeldridException"/>
        ///     naming the un-submitted command lists instead. Raise this only for a workload that legitimately keeps more in flight.
        /// </summary>
        public int MaxCommandBufferCount;
    }
}
