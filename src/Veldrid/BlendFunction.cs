namespace Veldrid
{
    /// <summary>
    ///     Controls how the source and destination factors are combined in a blend operation.
    /// </summary>
    public enum BlendFunction : byte
    {
        /// <summary>
        ///     Source and destination are added.
        /// </summary>
        Add = 0,

        /// <summary>
        ///     Destination is subtracted from source.
        /// </summary>
        Subtract = 1,

        /// <summary>
        ///     Source is subtracted from destination.
        /// </summary>
        ReverseSubtract = 2,

        /// <summary>
        ///     The minimum of source and destination is selected.
        /// </summary>
        Minimum = 3,

        /// <summary>
        ///     The maximum of source and destination is selected.
        /// </summary>
        Maximum = 4
    }
}
