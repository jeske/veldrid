namespace Veldrid
{
    /// <summary>
    ///     The type of a vertex element, describing how the element is interpreted.
    ///     NOTE: This enumeration is only meaningful for the Direct3D 11 backend.
    ///     When using Veldrid.SPIRV to cross-compile a vertex shader to HLSL, all vertex elements will
    ///     use <see cref="VertexElementSemantic.TextureCoordinate" />.
    /// </summary>
    public enum VertexElementSemantic : byte
    {
        /// <summary>
        ///     A position.
        /// </summary>
        Position = 0,

        /// <summary>
        ///     A normal direction.
        /// </summary>
        Normal = 1,

        /// <summary>
        ///     A texture coordinate.
        /// </summary>
        TextureCoordinate = 2,

        /// <summary>
        ///     A color.
        /// </summary>
        Color = 3
    }
}
