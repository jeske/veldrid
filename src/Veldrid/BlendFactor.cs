namespace Veldrid
{
    /// <summary>
    ///     Controls the influence of components in a blend operation.
    /// </summary>
    public enum BlendFactor : byte
    {
        /// <summary>
        ///     Each component is multiplied by 0.
        /// </summary>
        Zero = 0,

        /// <summary>
        ///     Each component is multiplied by 1.
        /// </summary>
        One = 1,

        /// <summary>
        ///     Each component is multiplied by the source alpha component.
        /// </summary>
        SourceAlpha = 2,

        /// <summary>
        ///     Each component is multiplied by (1 - source alpha).
        /// </summary>
        InverseSourceAlpha = 3,

        /// <summary>
        ///     Each component is multiplied by the destination alpha component.
        /// </summary>
        DestinationAlpha = 4,

        /// <summary>
        ///     Each component is multiplied by (1 - destination alpha).
        /// </summary>
        InverseDestinationAlpha = 5,

        /// <summary>
        ///     Each component is multiplied by the matching component of the source color.
        /// </summary>
        SourceColor = 6,

        /// <summary>
        ///     Each component is multiplied by (1 - the matching component of the source color).
        /// </summary>
        InverseSourceColor = 7,

        /// <summary>
        ///     Each component is multiplied by the matching component of the destination color.
        /// </summary>
        DestinationColor = 8,

        /// <summary>
        ///     Each component is multiplied by (1 - the matching component of the destination color).
        /// </summary>
        InverseDestinationColor = 9,

        /// <summary>
        ///     Each component is multiplied by the matching component in constant factor specified in
        ///     <see cref="BlendStateDescription.BlendFactor" />.
        /// </summary>
        BlendFactor = 10,

        /// <summary>
        ///     Each component is multiplied by (1 - the matching component in constant factor specified in
        ///     <see cref="BlendStateDescription.BlendFactor" />).
        /// </summary>
        InverseBlendFactor = 11
    }
}
