using System;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlSwapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public override bool IsDisposed => disposed;

        public CAMetalDrawable CurrentDrawable => drawable;

        public override bool SyncToVerticalBlank
        {
            get => syncToVerticalBlank;
            set
            {
                if (syncToVerticalBlank != value) setSyncToVerticalBlank(value);
            }
        }

        public override string Name { get; set; }
        private readonly MtlSwapchainFramebuffer framebuffer;
        private readonly MtlGraphicsDevice gd;
        private CAMetalLayer metalLayer;
        private UIView uiView; // Valid only when a UIViewSwapchainSource is used.
        private bool syncToVerticalBlank;
        private bool disposed;

        private CAMetalDrawable drawable;

        public MtlSwapchain(MtlGraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            syncToVerticalBlank = description.SyncToVerticalBlank;

            uint width;
            uint height;

            var source = description.Source;

            if (source is NSWindowSwapchainSource nsWindowSource)
            {
                var nswindow = new NSWindow(nsWindowSource.NSWindow);
                var contentView = nswindow.contentView;
                double backingScale = nswindow.backingScaleFactor;

                // drawableSize is in PIXELS. Honor the caller's requested pixel size (like the D3D11/Vulkan
                // backends do); only if none was given derive it from the content view's POINT frame x the
                // backing scale. Using the point frame directly produced a quarter-area drawable on Retina.
                ResolveMacOSDrawableSize(ref description, contentView.frame.size, backingScale, out width, out height);

                if (!CAMetalLayer.TryCast(contentView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    contentView.wantsLayer = true;
                    contentView.layer = metalLayer.NativePtr;
                }
                else
                {
                    // Adopted the window's existing layer — retain to balance the release in Dispose().
                    ObjectiveCRuntime.retain(metalLayer.NativePtr);
                }

                metalLayer.contentsScale = backingScale;
            }
            else if (source is NSViewSwapchainSource nsViewSource)
            {
                var contentView = new NSView(nsViewSource.NSView);
                // NSView.window.backingScaleFactor gives the correct Retina scale; a view not yet in a window has none.
                var viewWindow = contentView.window;
                double backingScale = viewWindow.NativePtr != IntPtr.Zero ? viewWindow.backingScaleFactor : 1.0;

                ResolveMacOSDrawableSize(ref description, contentView.frame.size, backingScale, out width, out height);

                if (!CAMetalLayer.TryCast(contentView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    contentView.wantsLayer = true;
                    contentView.layer = metalLayer.NativePtr;
                }
                else
                {
                    // Adopted the view's existing layer — retain to balance the release in Dispose().
                    ObjectiveCRuntime.retain(metalLayer.NativePtr);
                }

                if (viewWindow.NativePtr != IntPtr.Zero)
                    metalLayer.contentsScale = backingScale;
            }
            else if (source is UIViewSwapchainSource uiViewSource)
            {
                uiView = new UIView(uiViewSource.UIView);
                var viewSize = uiView.frame.size;
                width = (uint)viewSize.width;
                height = (uint)viewSize.height;

                if (!CAMetalLayer.TryCast(uiView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    metalLayer.frame = uiView.frame;
                    metalLayer.opaque = true;
                    uiView.layer.addSublayer(metalLayer.NativePtr);
                }
                else
                {
                    // Adopted the view's existing layer — retain to balance the release in Dispose().
                    ObjectiveCRuntime.retain(metalLayer.NativePtr);
                }
            }
            else
                throw new VeldridException("A Metal Swapchain can only be created from an NSWindow, NSView, or UIView.");

            var format = description.ColorSrgb
                ? PixelFormat.B8G8R8A8UNormSRgb
                : PixelFormat.B8G8R8A8UNorm;

            metalLayer.device = this.gd.Device;
            metalLayer.pixelFormat = MtlFormats.VdToMtlPixelFormat(format, false);
            // framebufferOnly=true enables a drawable optimization but makes the
            // drawable texture ILLEGAL as a blit/copy source (CopyTexture from the
            // swapchain silently produces nothing). Consumers that need screenshot
            // readback opt in via SwapchainDescription.AllowSwapchainReadback.
            metalLayer.framebufferOnly = !description.AllowSwapchainReadback;
            metalLayer.drawableSize = new CGSize(width, height);

            setSyncToVerticalBlank(syncToVerticalBlank);

            framebuffer = new MtlSwapchainFramebuffer(
                gd,
                this,
                description.DepthFormat,
                format);

            getNextDrawable();
        }

        #region Disposal

        public override void Dispose()
        {
            if (disposed) return;

            if (drawable.NativePtr != IntPtr.Zero) ObjectiveCRuntime.release(drawable.NativePtr);
            framebuffer.Dispose();
            ObjectiveCRuntime.release(metalLayer.NativePtr);

            disposed = true;
        }

        #endregion

        /// <summary>
        /// Resolve the swapchain's pixel size on macOS. <c>CAMetalLayer.drawableSize</c> is in PIXELS, whereas
        /// AppKit frames are in POINTS. Preference order:
        ///   1. The caller's explicit <see cref="SwapchainDescription.Width"/>/<see cref="SwapchainDescription.Height"/>
        ///      (same contract as the D3D11/Vulkan backends, which honor the description).
        ///   2. The view's point frame multiplied by the window's backing scale (hidden windows may have a frame
        ///      but no swapchain-resize event follows that would otherwise correct a point-sized drawable).
        /// </summary>
        private static void ResolveMacOSDrawableSize(
            ref SwapchainDescription description, CGSize viewFramePoints, double backingScale,
            out uint pixelWidth, out uint pixelHeight)
        {
            if (description.Width > 0 && description.Height > 0)
            {
                pixelWidth = description.Width;
                pixelHeight = description.Height;
                return;
            }

            if (!(backingScale > 0) || double.IsNaN(backingScale) || double.IsInfinity(backingScale)) backingScale = 1.0;
            pixelWidth = (uint)Math.Round(viewFramePoints.width * backingScale, MidpointRounding.AwayFromZero);
            pixelHeight = (uint)Math.Round(viewFramePoints.height * backingScale, MidpointRounding.AwayFromZero);
        }

        public override void Resize(uint width, uint height)
        {
            if (uiView.NativePtr != IntPtr.Zero)
                metalLayer.frame = uiView.frame;

            metalLayer.drawableSize = new CGSize(width, height);

            getNextDrawable();
        }

        public bool EnsureDrawableAvailable()
        {
            return !drawable.IsNull || getNextDrawable();
        }

        public void InvalidateDrawable()
        {
            ObjectiveCRuntime.release(drawable.NativePtr);
            drawable = default;
        }

        private bool getNextDrawable()
        {
            if (!drawable.IsNull) ObjectiveCRuntime.release(drawable.NativePtr);

            using (NSAutoreleasePool.Begin())
            {
                drawable = metalLayer.nextDrawable();

                if (!drawable.IsNull)
                {
                    ObjectiveCRuntime.retain(drawable.NativePtr);
                    framebuffer.UpdateTextures(drawable, metalLayer.drawableSize);
                    return true;
                }

                return false;
            }
        }

        private void setSyncToVerticalBlank(bool value)
        {
            syncToVerticalBlank = value;

            if (gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily1_v3
                || gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily1_v4
                || gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily2_v1)
                metalLayer.displaySyncEnabled = value;
        }
    }
}
