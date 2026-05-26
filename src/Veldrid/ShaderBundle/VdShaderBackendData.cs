using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// Shader data for a single backend within a .vdshader bundle.
    /// Contains entry points, shader file references (external) or inline base64 data.
    /// </summary>
    public sealed class VdShaderBackendData
    {
        /// <summary>Format of the shader data (hlsl_text, msl_text, glsl_text, spirv, dxbc, metallib).</summary>
        [JsonPropertyName("shaderFormat")]
        public string ShaderFormat { get; set; }

        /// <summary>Vertex shader entry point name.</summary>
        [JsonPropertyName("vertexEntryPoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VertexEntryPoint { get; set; }

        /// <summary>Fragment shader entry point name.</summary>
        [JsonPropertyName("fragmentEntryPoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FragmentEntryPoint { get; set; }

        /// <summary>Compute shader entry point name.</summary>
        [JsonPropertyName("computeEntryPoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ComputeEntryPoint { get; set; }

        /// <summary>External vertex shader filename (when payload is external).</summary>
        [JsonPropertyName("vertexShaderFile")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VertexShaderFile { get; set; }

        /// <summary>External fragment shader filename (when payload is external).</summary>
        [JsonPropertyName("fragmentShaderFile")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FragmentShaderFile { get; set; }

        /// <summary>External compute shader filename (when payload is external).</summary>
        [JsonPropertyName("computeShaderFile")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ComputeShaderFile { get; set; }

        /// <summary>Inline vertex shader data (base64-encoded).</summary>
        [JsonPropertyName("vertexShaderData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VertexShaderData { get; set; }

        /// <summary>Inline fragment shader data (base64-encoded).</summary>
        [JsonPropertyName("fragmentShaderData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FragmentShaderData { get; set; }

        /// <summary>Inline compute shader data (base64-encoded).</summary>
        [JsonPropertyName("computeShaderData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ComputeShaderData { get; set; }

        /// <summary>SHA256 hash of the compiled shader output bytes for this backend (validation).</summary>
        [JsonPropertyName("outputHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string OutputHash { get; set; }
    }
}
