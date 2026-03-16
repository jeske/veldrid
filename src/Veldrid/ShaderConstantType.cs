namespace Veldrid
{
    /// <summary>
    ///     The data type of a shader constant.
    /// </summary>
    public enum ShaderConstantType
    {
        /// <summary>
        ///     A boolean.
        /// </summary>
        Bool = 0,

        /// <summary>
        ///     A 16-bit unsigned integer.
        /// </summary>
        UInt16 = 1,

        /// <summary>
        ///     A 16-bit signed integer.
        /// </summary>
        Int16 = 2,

        /// <summary>
        ///     A 32-bit unsigned integer.
        /// </summary>
        UInt32 = 3,

        /// <summary>
        ///     A 32-bit signed integer.
        /// </summary>
        Int32 = 4,

        /// <summary>
        ///     A 64-bit unsigned integer.
        /// </summary>
        UInt64 = 5,

        /// <summary>
        ///     A 64-bit signed integer.
        /// </summary>
        Int64 = 6,

        /// <summary>
        ///     A 32-bit floating-point value.
        /// </summary>
        Float = 7,

        /// <summary>
        ///     A 64-bit floating-point value.
        /// </summary>
        Double = 8
    }
}
