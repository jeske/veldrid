// Legacy Veldrid 4.9.0 compatibility: Support both underscore and non-underscore naming
#define SUPPORT_LEGACY_VELDRID_SYMBOLS

namespace Veldrid
{
    /// <summary>
    ///     The format of data stored in a <see cref="Texture" />.
    ///     Each name is a compound identifier, where each component denotes a name and a number of bits used to store that
    ///     component. The final component identifies the storage type of each component. "Float" identifies a signed,
    ///     floating-point
    ///     type, UNorm identifies an unsigned integer type which is normalized, meaning it occupies the full space of the
    ///     integer
    ///     type. The SRgb suffix for normalized integer formats indicates that the RGB components are stored in sRGB format.
    /// </summary>
    public enum PixelFormat : byte
    {
        /// <summary>
        ///     RGBA component order. Each component is an 8-bit unsigned normalized integer.
        /// </summary>
        R8G8B8A8UNorm = 0,

        /// <summary>
        ///     BGRA component order. Each component is an 8-bit unsigned normalized integer.
        /// </summary>
        B8G8R8A8UNorm = 1,

        /// <summary>
        ///     Single-channel, 8-bit unsigned normalized integer.
        /// </summary>
        R8UNorm = 2,

        /// <summary>
        ///     Single-channel, 16-bit unsigned normalized integer. Can be used as a depth format.
        /// </summary>
        R16UNorm = 3,

        /// <summary>
        ///     RGBA component order. Each component is a 32-bit signed floating-point value.
        /// </summary>
        R32G32B32A32Float = 4,

        /// <summary>
        ///     Single-channel, 32-bit signed floating-point value. Can be used as a depth format.
        /// </summary>
        R32Float = 5,

        /// <summary>
        ///     BC3 block compressed format.
        /// </summary>
        Bc3UNorm = 6,

        /// <summary>
        ///     A depth-stencil format where the depth is stored in a 24-bit unsigned normalized integer, and the stencil is stored
        ///     in an 8-bit unsigned integer.
        /// </summary>
        D24UNormS8UInt = 7,

        /// <summary>
        ///     A depth-stencil format where the depth is stored in a 32-bit signed floating-point value, and the stencil is stored
        ///     in an 8-bit unsigned integer.
        /// </summary>
        D32FloatS8UInt = 8,

        /// <summary>
        ///     RGBA component order. Each component is a 32-bit unsigned integer.
        /// </summary>
        R32G32B32A32UInt = 9,

        /// <summary>
        ///     RG component order. Each component is an 8-bit signed normalized integer.
        /// </summary>
        R8G8SNorm = 10,

        /// <summary>
        ///     BC1 block compressed format with no alpha.
        /// </summary>
        Bc1RgbUNorm = 11,

        /// <summary>
        ///     BC1 block compressed format with a single-bit alpha channel.
        /// </summary>
        Bc1RgbaUNorm = 12,

        /// <summary>
        ///     BC2 block compressed format.
        /// </summary>
        Bc2UNorm = 13,

        /// <summary>
        ///     A 32-bit packed format. The 10-bit R component occupies bits 0..9, the 10-bit G component occupies bits 10..19,
        ///     the 10-bit A component occupies 20..29, and the 2-bit A component occupies bits 30..31. Each value is an unsigned,
        ///     normalized integer.
        /// </summary>
        R10G10B10A2UNorm = 14,

        /// <summary>
        ///     A 32-bit packed format. The 10-bit R component occupies bits 0..9, the 10-bit G component occupies bits 10..19,
        ///     the 10-bit A component occupies 20..29, and the 2-bit A component occupies bits 30..31. Each value is an unsigned
        ///     integer.
        /// </summary>
        R10G10B10A2UInt = 15,

        /// <summary>
        ///     A 32-bit packed format. The 11-bit R componnent occupies bits 0..10, the 11-bit G component occupies bits 11..21,
        ///     and the 10-bit B component occupies bits 22..31. Each value is an unsigned floating point value.
        /// </summary>
        R11G11B10Float = 16,

        /// <summary>
        ///     Single-channel, 8-bit signed normalized integer.
        /// </summary>
        R8SNorm = 17,

        /// <summary>
        ///     Single-channel, 8-bit unsigned integer.
        /// </summary>
        R8UInt = 18,

        /// <summary>
        ///     Single-channel, 8-bit signed integer.
        /// </summary>
        R8SInt = 19,

        /// <summary>
        ///     Single-channel, 16-bit signed normalized integer.
        /// </summary>
        R16SNorm = 20,

        /// <summary>
        ///     Single-channel, 16-bit unsigned integer.
        /// </summary>
        R16UInt = 21,

        /// <summary>
        ///     Single-channel, 16-bit signed integer.
        /// </summary>
        R16SInt = 22,

        /// <summary>
        ///     Single-channel, 16-bit signed floating-point value.
        /// </summary>
        R16Float = 23,

        /// <summary>
        ///     Single-channel, 32-bit unsigned integer
        /// </summary>
        R32UInt = 24,

        /// <summary>
        ///     Single-channel, 32-bit signed integer
        /// </summary>
        R32SInt = 25,

        /// <summary>
        ///     RG component order. Each component is an 8-bit unsigned normalized integer.
        /// </summary>
        R8G8UNorm = 26,

        /// <summary>
        ///     RG component order. Each component is an 8-bit unsigned integer.
        /// </summary>
        R8G8UInt = 27,

        /// <summary>
        ///     RG component order. Each component is an 8-bit signed integer.
        /// </summary>
        R8G8SInt = 28,

        /// <summary>
        ///     RG component order. Each component is a 16-bit unsigned normalized integer.
        /// </summary>
        R16G16UNorm = 29,

        /// <summary>
        ///     RG component order. Each component is a 16-bit signed normalized integer.
        /// </summary>
        R16G16SNorm = 30,

        /// <summary>
        ///     RG component order. Each component is a 16-bit unsigned integer.
        /// </summary>
        R16G16UInt = 31,

        /// <summary>
        ///     RG component order. Each component is a 16-bit signed integer.
        /// </summary>
        R16G16SInt = 32,

        /// <summary>
        ///     RG component order. Each component is a 16-bit signed floating-point value.
        /// </summary>
        R16G16Float = 33,

        /// <summary>
        ///     RG component order. Each component is a 32-bit unsigned integer.
        /// </summary>
        R32G32UInt = 34,

        /// <summary>
        ///     RG component order. Each component is a 32-bit signed integer.
        /// </summary>
        R32G32SInt = 35,

        /// <summary>
        ///     RG component order. Each component is a 32-bit signed floating-point value.
        /// </summary>
        R32G32Float = 36,

        /// <summary>
        ///     RGBA component order. Each component is an 8-bit signed normalized integer.
        /// </summary>
        R8G8B8A8SNorm = 37,

        /// <summary>
        ///     RGBA component order. Each component is an 8-bit unsigned integer.
        /// </summary>
        R8G8B8A8UInt = 38,

        /// <summary>
        ///     RGBA component order. Each component is an 8-bit signed integer.
        /// </summary>
        R8G8B8A8SInt = 39,

        /// <summary>
        ///     RGBA component order. Each component is a 16-bit unsigned normalized integer.
        /// </summary>
        R16G16B16A16UNorm = 40,

        /// <summary>
        ///     RGBA component order. Each component is a 16-bit signed normalized integer.
        /// </summary>
        R16G16B16A16SNorm = 41,

        /// <summary>
        ///     RGBA component order. Each component is a 16-bit unsigned integer.
        /// </summary>
        R16G16B16A16UInt = 42,

        /// <summary>
        ///     RGBA component order. Each component is a 16-bit signed integer.
        /// </summary>
        R16G16B16A16SInt = 43,

        /// <summary>
        ///     RGBA component order. Each component is a 16-bit floating-point value.
        /// </summary>
        R16G16B16A16Float = 44,

        /// <summary>
        ///     RGBA component order. Each component is a 32-bit signed integer.
        /// </summary>
        R32G32B32A32SInt = 45,

        /// <summary>
        ///     A 64-bit, 4x4 block-compressed format storing unsigned normalized RGB data.
        /// </summary>
        Etc2R8G8B8UNorm = 46,

        /// <summary>
        ///     A 64-bit, 4x4 block-compressed format storing unsigned normalized RGB data, as well as 1 bit of alpha data.
        /// </summary>
        Etc2R8G8B8A1UNorm = 47,

        /// <summary>
        ///     A 128-bit, 4x4 block-compressed format storing 64 bits of unsigned normalized RGB data, as well as 64 bits of alpha
        ///     data.
        /// </summary>
        Etc2R8G8B8A8UNorm = 48,

        /// <summary>
        ///     BC4 block compressed format, unsigned normalized values.
        /// </summary>
        Bc4UNorm = 49,

        /// <summary>
        ///     BC4 block compressed format, signed normalized values.
        /// </summary>
        Bc4SNorm = 50,

        /// <summary>
        ///     BC5 block compressed format, unsigned normalized values.
        /// </summary>
        Bc5UNorm = 51,

        /// <summary>
        ///     BC5 block compressed format, signed normalized values.
        /// </summary>
        Bc5SNorm = 52,

        /// <summary>
        ///     BC7 block compressed format.
        /// </summary>
        Bc7UNorm = 53,

        /// <summary>
        ///     RGBA component order. Each component is an 8-bit unsigned normalized integer.
        ///     This is an sRGB format.
        /// </summary>
        R8G8B8A8UNormSRgb = 54,

        /// <summary>
        ///     BGRA component order. Each component is an 8-bit unsigned normalized integer.
        ///     This is an sRGB format.
        /// </summary>
        B8G8R8A8UNormSRgb = 55,

        /// <summary>
        ///     BC1 block compressed format with no alpha.
        ///     This is an sRGB format.
        /// </summary>
        Bc1RgbUNormSRgb = 56,

        /// <summary>
        ///     BC1 block compressed format with a single-bit alpha channel.
        ///     This is an sRGB format.
        /// </summary>
        Bc1RgbaUNormSRgb = 57,

        /// <summary>
        ///     BC2 block compressed format.
        ///     This is an sRGB format.
        /// </summary>
        Bc2UNormSRgb = 58,

        /// <summary>
        ///     BC3 block compressed format.
        ///     This is an sRGB format.
        /// </summary>
        Bc3UNormSRgb = 59,

        /// <summary>
        ///     BC7 block compressed format.
        ///     This is an sRGB format.
        /// </summary>
        Bc7UNormSRgb = 60,

#if SUPPORT_LEGACY_VELDRID_SYMBOLS
        // Legacy Veldrid 4.9.0 symbol aliases (with underscores)
        // These allow old code to compile without changes
        R8_UNorm = R8UNorm,
        R16_UNorm = R16UNorm,
        R8_G8_B8_A8_UNorm = R8G8B8A8UNorm,
        B8_G8_R8_A8_UNorm = B8G8R8A8UNorm,
        R16_G16_B16_A16_UInt = R16G16B16A16UInt,
        R16_G16_B16_A16_UNorm = R16G16B16A16UNorm,
        R32_Float = R32Float,
        R32_G32_Float = R32G32Float,
        R32_G32_B32_A32_Float = R32G32B32A32Float,
        D24_UNorm_S8_UInt = D24UNormS8UInt,
        D32_Float_S8_UInt = D32FloatS8UInt,
        R8_G8_B8_A8_UNorm_SRgb = R8G8B8A8UNormSRgb,
        B8_G8_R8_A8_UNorm_SRgb = B8G8R8A8UNormSRgb
#endif
    }
}
