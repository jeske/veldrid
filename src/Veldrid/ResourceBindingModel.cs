namespace Veldrid
{
    /// <summary>
    ///     Identifies a particular binding model used when connecting elements in a <see cref="ResourceLayout" /> with
    ///     resources
    ///     defined in API-specific shader code.
    /// </summary>
    public enum ResourceBindingModel
    {
        /// <summary>
        ///     The default binding model.
        /// </summary>
        Default = 0,

        /// <summary>
        ///     An improved binding model which enables greater shader reuse and authoring flexibility.
        /// </summary>
        Improved = 1,

        /// <summary>
        ///     The shader bundle controls all binding slot assignments. The runtime uses the exact
        ///     buffer/texture/sampler indices declared by the shader compiler in the .vdshader flat binding map.
        ///     No sequential index computation, no base offsets — the binding map IS the source of truth.
        ///     This is automatically set when using <see cref="ResourceFactory.CreateFromBundle"/> with
        ///     layouts created via <see cref="PrecompiledShaderResult.CreateResourceLayouts"/>.
        /// </summary>
        ShaderBundleControlled = 2
    }
}
