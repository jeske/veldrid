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
                var windowContentSize = contentView.frame.size;
                width = (uint)windowContentSize.width;
                height = (uint)windowContentSize.height;

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

                metalLayer.contentsScale = nswindow.backingScaleFactor;
            }
            else if (source is NSViewSwapchainSource nsViewSource)
            {
                var contentView = new NSView(nsViewSource.NSView);
                var windowContentSize = contentView.frame.size;
                width = (uint)windowContentSize.width;
                height = (uint)windowContentSize.height;

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

                // Set contentsScale from the view's window backingScaleFactor for Retina support.
                // NSView.window.backingScaleFactor gives the correct scale on macOS.
                var viewWindow = contentView.window;
                if (viewWindow.NativePtr != IntPtr.Zero)
                    metalLayer.contentsScale = viewWindow.backingScaleFactor;
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
