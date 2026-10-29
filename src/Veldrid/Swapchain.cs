using System;

namespace Veldrid
{
    /// <summary>
    ///     A device resource providing the ability to present rendered images to a visible surface.
    ///     See <see cref="SwapchainDescription" />.
    /// </summary>
    public abstract class Swapchain : IDeviceResource, IDisposable
    {
        /// <summary>
        ///     Gets a <see cref="Framebuffer" /> representing the render targets of this instance.
        /// </summary>
        public abstract Framebuffer Framebuffer { get; }

        /// <summary>
        ///     A bool indicating whether this instance has been disposed.
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        ///     Gets or sets whether presentation of this Swapchain will be synchronized to the window system's vertical refresh
        ///     rate.
        /// </summary>
        public abstract bool SyncToVerticalBlank { get; set; }

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

        /// <summary>
        ///     Gets or sets the compositor background color for areas not covered by the backbuffer
        ///     when <see cref="SwapchainScalingMode.None"/> is used. Platform-specific; no-op on
        ///     backends that don't support it (Vulkan, OpenGL).
        /// </summary>
        public virtual RgbaFloat? CompositorBackgroundColor { get; set; }

        /// <summary>
        ///     Resizes the renderable Textures managed by this instance to the given dimensions.
        /// </summary>
        /// <param name="width">The new width of the Swapchain.</param>
        /// <param name="height">The new height of the Swapchain.</param>
        public abstract void Resize(uint width, uint height);

        /// <summary>
        ///     Rebuilds the native swapchain against the owning device AFTER a successful
        ///     <see cref="GraphicsDevice.RecoverFromDeviceLost"/>. The managed object keeps its identity and its original
        ///     <see cref="SwapchainSource"/>; the native swapchain, back buffer and <see cref="Framebuffer"/> are all new.
        ///     Disposal of the dead native objects is best-effort (they belong to a device that no longer exists).
        ///     Must be followed by <see cref="Resize"/> if the window size may have changed during the outage.
        /// </summary>
        /// <exception cref="NotSupportedException">The backend does not support in-place device recovery.</exception>
        public virtual void RecreateOnRestoredDevice()
        {
            throw new NotSupportedException("This swapchain backend does not support in-place device recovery.");
        }
    }
}
