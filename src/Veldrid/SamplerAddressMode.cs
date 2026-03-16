namespace Veldrid
{
    /// <summary>
    ///     An addressing mode for texture coordinates.
    /// </summary>
    public enum SamplerAddressMode : byte
    {
        /// <summary>
        ///     Texture coordinates are wrapped upon overflow.
        /// </summary>
        Wrap = 0,

        /// <summary>
        ///     Texture coordinates are mirrored upon overflow.
        /// </summary>
        Mirror = 1,

        /// <summary>
        ///     Texture coordinates are clamped to the maximum or minimum values upon overflow.
        /// </summary>
        Clamp = 2,

        /// <summary>
        ///     Texture coordinates that overflow return the predefined border color defined in
        ///     <see cref="SamplerDescription.BorderColor" />.
        /// </summary>
        Border = 3
    }
}
