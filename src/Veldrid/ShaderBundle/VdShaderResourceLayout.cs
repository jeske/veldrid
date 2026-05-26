using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// Serializable representation of a <see cref="ResourceLayoutDescription"/> within a .vdshader bundle.
    /// Array index corresponds to the descriptor set number.
    /// </summary>
    public sealed class VdShaderResourceLayout
    {
        /// <summary>The elements in this resource layout.</summary>
        [JsonPropertyName("elements")]
        public VdShaderResourceElement[] Elements { get; set; } = [];
    }

    /// <summary>
    /// Serializable representation of a <see cref="ResourceLayoutElementDescription"/> within a .vdshader bundle.
    /// </summary>
    public sealed class VdShaderResourceElement
    {
        /// <summary>The name of the resource.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>The kind of resource (UniformBuffer, TextureReadOnly, Sampler, etc.).</summary>
        [JsonPropertyName("kind")]
        [JsonConverter(typeof(JsonStringEnumConverter<ResourceKind>))]
        public ResourceKind Kind { get; set; }

        /// <summary>Which shader stages use this resource.</summary>
        [JsonPropertyName("stages")]
        [JsonConverter(typeof(JsonStringEnumConverter<ShaderStages>))]
        public ShaderStages Stages { get; set; }
    }
}
