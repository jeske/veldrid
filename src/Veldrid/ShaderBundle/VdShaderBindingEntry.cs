using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// Serializable representation of a binding map entry within a .vdshader bundle.
    /// Records the mapping from GLSL (set, binding) to the flat register/argument index
    /// assigned during SPIR-V cross-compilation.
    /// </summary>
    public sealed class VdShaderBindingEntry
    {
        /// <summary>The resource name (informational).</summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Name { get; set; }

        /// <summary>The GLSL descriptor set number.</summary>
        [JsonPropertyName("set")]
        public uint Set { get; set; }

        /// <summary>The GLSL binding number within the descriptor set.</summary>
        [JsonPropertyName("binding")]
        public uint Binding { get; set; }

        /// <summary>The resource kind (UniformBuffer, TextureReadOnly, Sampler, etc.).</summary>
        [JsonPropertyName("kind")]
        [JsonConverter(typeof(JsonStringEnumConverter<ResourceKind>))]
        public ResourceKind Kind { get; set; }

        /// <summary>Which shader stages use this resource.</summary>
        [JsonPropertyName("stages")]
        [JsonConverter(typeof(JsonStringEnumConverter<ShaderStages>))]
        public ShaderStages Stages { get; set; }

        /// <summary>
        /// The flat register/argument index assigned by the cross-compiler.
        /// On D3D11: the HLSL register index (bN, tN, sN, or uN).
        /// On Metal: the argument table index (buffer(N), texture(N), or sampler(N)).
        /// </summary>
        [JsonPropertyName("flatIndex")]
        public uint FlatIndex { get; set; }
    }
}
