// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlSwapchainTexture : MtlTexture
    {
        public override MTLTexture DeviceTexture => deviceTexture;

        public override uint Width => width;

        public override uint Height => height;

        public override uint Depth => 1;

        public override uint ArrayLayers => 1;

        public override uint MipLevels => 1;

        public override TextureUsage Usage => TextureUsage.RenderTarget;

        public override TextureType Type => TextureType.Texture2D;

        public override TextureSampleCount SampleCount => TextureSampleCount.Count1;

        public override PixelFormat Format => format;

        public override MTLPixelFormat MtlPixelFormat => mtlPixelFormat;

        public override MTLTextureType MtlTextureType => MTLTextureType.Type2D;

        private MTLTexture deviceTexture;
        private uint width;
        private uint height;
        private PixelFormat format;
        private MTLPixelFormat mtlPixelFormat;

        public void SetDrawable(CAMetalDrawable drawable, CGSize size, PixelFormat format)
        {
            deviceTexture = drawable.texture;
            width = (uint)size.width;
            height = (uint)size.height;
            // BUGFIX: previously used the (never-assigned) Format property here, which
            // defaulted to R8G8B8A8UNorm — misreporting the swapchain texture's format
            // (the CAMetalLayer drawable is BGRA8) and breaking CPU readback swizzles.
            this.format = format;
            mtlPixelFormat = MtlFormats.VdToMtlPixelFormat(format, false);
        }
    }
}
