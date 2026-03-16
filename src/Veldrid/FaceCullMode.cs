namespace Veldrid
{
    /// <summary>
    ///     Indicates which face will be culled.
    /// </summary>
    public enum FaceCullMode : byte
    {
        /// <summary>
        ///     The back face.
        /// </summary>
        Back = 0,

        /// <summary>
        ///     The front face.
        /// </summary>
        Front = 1,

        /// <summary>
        ///     No face culling.
        /// </summary>
        None = 2
    }
}
