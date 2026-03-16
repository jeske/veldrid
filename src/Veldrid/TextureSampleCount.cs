namespace Veldrid
{
    /// <summary>
    ///     Describes the number of samples to use in a <see cref="Texture" />.
    /// </summary>
    public enum TextureSampleCount : byte
    {
        /// <summary>
        ///     1 sample (no multi-sampling).
        /// </summary>
        Count1 = 0,

        /// <summary>
        ///     2 Samples.
        /// </summary>
        Count2 = 1,

        /// <summary>
        ///     4 Samples.
        /// </summary>
        Count4 = 2,

        /// <summary>
        ///     8 Samples.
        /// </summary>
        Count8 = 3,

        /// <summary>
        ///     16 Samples.
        /// </summary>
        Count16 = 4,

        /// <summary>
        ///     32 Samples.
        /// </summary>
        Count32 = 5
    }
}
