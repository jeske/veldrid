namespace Veldrid
{
    /// <summary>
    ///     The specific graphics API used by the <see cref="GraphicsDevice" />.
    /// </summary>
    public enum GraphicsBackend : byte
    {
        /// <summary>
        ///     Direct3D 11.
        /// </summary>
        Direct3D11 = 0,

        /// <summary>
        ///     Vulkan.
        /// </summary>
        Vulkan = 1,

        /// <summary>
        ///     OpenGL.
        /// </summary>
        OpenGL = 2,

        /// <summary>
        ///     Metal.
        /// </summary>
        Metal = 3,

        /// <summary>
        ///     OpenGL ES.
        /// </summary>
        OpenGLES = 4
    }
}
