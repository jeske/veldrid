using System;

namespace Veldrid
{
    /// <summary>
    ///     A <see cref="Pipeline" /> component describing a full set of shader stages and vertex layouts.
    /// </summary>
    public struct ShaderSetDescription : IEquatable<ShaderSetDescription>
    {
        /// <summary>
        ///     An array of <see cref="VertexLayoutDescription" /> describing the set of vertex layouts understood by the
        ///     <see cref="Pipeline" />. Each element in this array describes the input layout of a single
        ///     <see cref="DeviceBuffer" />
        ///     to be bound when drawing.
        /// </summary>
        public VertexLayoutDescription[] VertexLayouts;

        /// <summary>
        ///     An array of <see cref="Shader" /> objects, one for each shader stage which is to be active in the
        ///     <see cref="Pipeline" />. At a minimum, every graphics Pipeline must include a Vertex and Fragment
        ///     shader. All other stages are optional, but if either Tessellation stage is present, then the other must also be.
        /// </summary>
        public Shader[] Shaders;

        /// <summary>
        ///     An array of <see cref="SpecializationConstant" /> used to override specialization constants in the created
        ///     <see cref="Pipeline" />. Each element in this array describes a single ID-value pair, which will be matched with
        ///     the
        ///     constants specified in each <see cref="Shader" />.
        /// </summary>
        public SpecializationConstant[] Specializations;

        /// <summary>The shape whose vertex inputs built this description (null for Veldrid-internal raw sets).</summary>
        internal ShaderPipelineShape VerifiedShape;

        /// <summary>
        ///     Constructs a new ShaderSetDescription.
        /// </summary>
        /// <param name="vertexLayouts">
        ///     An array of <see cref="VertexLayoutDescription" /> describing the set of vertex layouts
        ///     understood by the <see cref="Pipeline" />. Each element in this array describes the input layout of a single
        ///     <see cref="DeviceBuffer" /> to be bound when drawing.
        /// </param>
        /// <param name="shaders">
        ///     An array of <see cref="Shader" /> objects, one for each shader stage which is to be active
        ///     in the <see cref="Pipeline" />. At a minimum, every graphics Pipeline must include a Vertex and Fragment shader.
        ///     All
        ///     other stages are optional, but if either Tessellation stage is present, then the other must also be.
        /// </param>
        internal ShaderSetDescription(VertexLayoutDescription[] vertexLayouts, Shader[] shaders)
        {
            VertexLayouts = vertexLayouts;
            Shaders = shaders;
            Specializations = null;
            VerifiedShape = null;
        }

        /// <summary>
        ///     Constructs a new ShaderSetDescription from a shape's verified vertex inputs - the SAME call as before, with each positional
        ///     vertex layout replaced by the typed <see cref="VertexInput"/> that owns it (array position was the vertex slot index).
        ///     All inputs must belong to one shape and appear in slot order; the shaders must be that shape's bundle shaders.
        /// </summary>
        /// <param name="vertexInputs">The shape's <c>VertexInput</c> fields, in slot order (EMPTY for shaders with no vertex attributes - fullscreen triangles from <c>gl_VertexIndex</c>; the pipeline's shape then comes from its layouts).</param>
        /// <param name="shaders">The shape's bundle shaders (<c>shape.Bundle.Shaders</c>).</param>
        /// <param name="specializations">Specialization constants, if the GLSL declares any.</param>
        public ShaderSetDescription(VertexInput[] vertexInputs, Shader[] shaders, SpecializationConstant[] specializations = null)
        {
            ArgumentNullException.ThrowIfNull(vertexInputs);
            ArgumentNullException.ThrowIfNull(shaders);
            var violations = new System.Collections.Generic.List<ShaderContractViolation>();
            ShaderPipelineShape shape = null;
            for (int i = 0; i < vertexInputs.Length; i++)
            {
                var input = vertexInputs[i];
                if (input is null) { violations.Add(new ShaderContractViolation("SHADERSET", $"[{i}]", "null", "a shape VertexInput", "vertex layouts come from shape fields")); continue; }
                if (input.Owner is null) { violations.Add(new ShaderContractViolation("SHADERSET", $"[{i}]", "unbound VertexInput", "field of a Bind()-created shape", "vertex inputs come from ShaderPipelineShape.Bind")); continue; }
                shape ??= input.Owner;
                if (!ReferenceEquals(input.Owner, shape)) violations.Add(new ShaderContractViolation("SHADERSET", $"[{i}]", $"input of {input.Owner.GetType().Name}", shape.GetType().Name, "one shader set binds one shape's inputs"));
                else if (input.SlotIndex != i) violations.Add(new ShaderContractViolation("SHADERSET", $"[{i}]", $"slot {input.SlotIndex}", $"slot {i}", "vertex inputs are listed in slot order"));
            }
            if (violations.Count > 0) throw new ShaderContractException("shader set", violations);
            if (vertexInputs.Length == 0)
            {
                VertexLayouts = Array.Empty<VertexLayoutDescription>();
                Shaders = shaders;
                Specializations = specializations;
                VerifiedShape = null;   // established by the pipeline's layouts in ResourceFactory.CreateGraphicsPipeline
                return;
            }
            shape.EnsureBound();
            if (vertexInputs.Length != shape.VertexInputCount)
                violations.Add(new ShaderContractViolation("SHADERSET", "vertexInputs", $"{vertexInputs.Length}", $"{shape.VertexInputCount}", "every VertexInput of the shape must be listed"));
            if (!Util.ArrayEquals(shaders, shape.Bundle.Shaders))
                violations.Add(new ShaderContractViolation(ShaderContractVerifier.Rule_V7_ShaderSet, "shaders", "different Shader objects", "shape.Bundle.Shaders", "the shaders must be the verified bundle's shaders"));
            if (violations.Count > 0) throw new ShaderContractException(shape.BundleName, violations);
            VertexLayouts = new VertexLayoutDescription[vertexInputs.Length];
            for (int i = 0; i < vertexInputs.Length; i++) VertexLayouts[i] = vertexInputs[i].Layout;
            Shaders = shaders;
            Specializations = specializations;
            VerifiedShape = shape;
        }

        /// <summary>
        ///     Constructs a new ShaderSetDescription.
        /// </summary>
        /// <param name="vertexLayouts">
        ///     An array of <see cref="VertexLayoutDescription" /> describing the set of vertex layouts
        ///     understood by the <see cref="Pipeline" />. Each element in this array describes the input layout of a single
        ///     <see cref="DeviceBuffer" /> to be bound when drawing.
        /// </param>
        /// <param name="shaders">
        ///     An array of <see cref="Shader" /> objects, one for each shader stage which is to be active
        ///     in the <see cref="Pipeline" />. At a minimum, every graphics Pipeline must include a Vertex and Fragment shader.
        ///     All
        ///     other stages are optional, but if either Tessellation stage is present, then the other must also be.
        /// </param>
        /// <param name="specializations">
        ///     An array of <see cref="SpecializationConstant" /> used to override specialization
        ///     constants in the created <see cref="Pipeline" />. Each element in this array describes a single ID-value pair,
        ///     which
        ///     will be matched with the constants specified in each <see cref="Shader" />.
        /// </param>
        internal ShaderSetDescription(
            VertexLayoutDescription[] vertexLayouts,
            Shader[] shaders,
            SpecializationConstant[] specializations)
        {
            VertexLayouts = vertexLayouts;
            Shaders = shaders;
            Specializations = specializations;
            VerifiedShape = null;
        }

        /// <summary>
        ///     Element-wise equality.
        /// </summary>
        /// <param name="other">The instance to compare to.</param>
        /// <returns>True if all array elements are equal; false otherswise.</returns>
        public bool Equals(ShaderSetDescription other)
        {
            return Util.ArrayEqualsEquatable(VertexLayouts, other.VertexLayouts)
                   && Util.ArrayEquals(Shaders, other.Shaders)
                   && Util.ArrayEqualsEquatable(Specializations, other.Specializations);
        }

        /// <summary>
        ///     Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
        public override int GetHashCode()
        {
            return HashHelper.Combine(
                HashHelper.Array(VertexLayouts),
                HashHelper.Array(Shaders),
                HashHelper.Array(Specializations));
        }
    }
}
