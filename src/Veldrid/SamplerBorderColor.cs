namespace Veldrid
{
    /// <summary>
    ///     A constant color that is sampled when <see cref="SamplerAddressMode.Border" /> is used.
    /// </summary>
    public enum SamplerBorderColor : byte
    {
        /// <summary>
        ///     Transparent Black (0, 0, 0, 0)
        /// </summary>
        TransparentBlack = 0,

        /// <summary>
        ///     Opaque Black (0, 0, 0, 1)
        /// </summary>
        OpaqueBlack = 1,

        /// <summary>
        ///     Opaque White (1, 1, 1, 1)
        /// </summary>
        OpaqueWhite = 2
    }
}
