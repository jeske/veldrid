// Legacy Veldrid 4.9.0 compatibility: Support both underscore and non-underscore naming
#define SUPPORT_LEGACY_VELDRID_SYMBOLS

namespace Veldrid
{
    /// <summary>
    ///     The format of an individual vertex element.
    /// </summary>
    public enum VertexElementFormat : byte
    {
        /// <summary>
        ///     One 32-bit floating point value.
        /// </summary>
        Float1 = 0,

        /// <summary>
        ///     Two 32-bit floating point values.
        /// </summary>
        Float2 = 1,

        /// <summary>
        ///     Three 32-bit floating point values.
        /// </summary>
        Float3 = 2,

        /// <summary>
        ///     Four 32-bit floating point values.
        /// </summary>
        Float4 = 3,

        /// <summary>
        ///     Two 8-bit unsigned normalized integers.
        /// </summary>
        Byte2Norm = 4,

        /// <summary>
        ///     Two 8-bit unisgned integers.
        /// </summary>
        Byte2 = 5,

        /// <summary>
        ///     Four 8-bit unsigned normalized integers.
        /// </summary>
        Byte4Norm = 6,

        /// <summary>
        ///     Four 8-bit unsigned integers.
        /// </summary>
        Byte4 = 7,

        /// <summary>
        ///     Two 8-bit signed normalized integers.
        /// </summary>
        SByte2Norm = 8,

        /// <summary>
        ///     Two 8-bit signed integers.
        /// </summary>
        SByte2 = 9,

        /// <summary>
        ///     Four 8-bit signed normalized integers.
        /// </summary>
        SByte4Norm = 10,

        /// <summary>
        ///     Four 8-bit signed integers.
        /// </summary>
        SByte4 = 11,

        /// <summary>
        ///     Two 16-bit unsigned normalized integers.
        /// </summary>
        UShort2Norm = 12,

        /// <summary>
        ///     Two 16-bit unsigned integers.
        /// </summary>
        UShort2 = 13,

        /// <summary>
        ///     Four 16-bit unsigned normalized integers.
        /// </summary>
        UShort4Norm = 14,

        /// <summary>
        ///     Four 16-bit unsigned integers.
        /// </summary>
        UShort4 = 15,

        /// <summary>
        ///     Two 16-bit signed normalized integers.
        /// </summary>
        Short2Norm = 16,

        /// <summary>
        ///     Two 16-bit signed integers.
        /// </summary>
        Short2 = 17,

        /// <summary>
        ///     Four 16-bit signed normalized integers.
        /// </summary>
        Short4Norm = 18,

        /// <summary>
        ///     Four 16-bit signed integers.
        /// </summary>
        Short4 = 19,

        /// <summary>
        ///     One 32-bit unsigned integer.
        /// </summary>
        UInt1 = 20,

        /// <summary>
        ///     Two 32-bit unsigned integers.
        /// </summary>
        UInt2 = 21,

        /// <summary>
        ///     Three 32-bit unsigned integers.
        /// </summary>
        UInt3 = 22,

        /// <summary>
        ///     Four 32-bit unsigned integers.
        /// </summary>
        UInt4 = 23,

        /// <summary>
        ///     One 32-bit signed integer.
        /// </summary>
        Int1 = 24,

        /// <summary>
        ///     Two 32-bit signed integers.
        /// </summary>
        Int2 = 25,

        /// <summary>
        ///     Three 32-bit signed integers.
        /// </summary>
        Int3 = 26,

        /// <summary>
        ///     Four 32-bit signed integers.
        /// </summary>
        Int4 = 27,

        /// <summary>
        ///     One 16-bit floating point value.
        /// </summary>
        Half1 = 28,

        /// <summary>
        ///     Two 16-bit floating point values.
        /// </summary>
        Half2 = 29,

        /// <summary>
        ///     Four 16-bit floating point values.
        /// </summary>
        Half4 = 30,

#if SUPPORT_LEGACY_VELDRID_SYMBOLS
        // Legacy Veldrid 4.9.0 symbol aliases (with underscores)
        Byte2_Norm = Byte2Norm,
        Byte4_Norm = Byte4Norm,
        SByte2_Norm = SByte2Norm,
        SByte4_Norm = SByte4Norm,
        UShort2_Norm = UShort2Norm,
        UShort4_Norm = UShort4Norm,
        Short2_Norm = Short2Norm,
        Short4_Norm = Short4Norm
#endif
    }
}
