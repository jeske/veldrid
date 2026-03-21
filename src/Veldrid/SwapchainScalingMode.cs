namespace Veldrid
{
    /// <summary>
    /// Controls how the compositor handles the swapchain backbuffer when it doesn't
    /// match the window size (e.g., during a resize before the app re-renders).
    /// </summary>
    public enum SwapchainScalingMode
    {
        /// <summary>
        /// The compositor stretches the backbuffer to fill the window.
        /// Best for full-canvas 3D games — resize appears instant.
        /// This is the default and matches legacy behavior.
        /// </summary>
        Stretch = 0,

        /// <summary>
        /// The compositor shows the backbuffer at its original size (top-left aligned)
        /// and fills the remaining area with the compositor background color.
        /// Best for desktop UI applications — no distortion of controls or text.
        /// </summary>
        None = 1,
    }
}
