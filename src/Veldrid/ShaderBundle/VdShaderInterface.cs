using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// The compiled INTERFACE of a .vdshader bundle, by NAME (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md D5):
    /// what the GPU will actually bind. Written by Veldrid.SPIRV from post-optimisation SPIR-V reflection; read by
    /// <see cref="ShaderContractVerifier"/> at pipeline creation (V7). A bundle without it is rejected (format version 2).
    /// </summary>
    public sealed class VdShaderInterface
    {
        /// <summary>Vertex stage inputs: the compiled `in` variables, by location.</summary>
        [JsonPropertyName("vertexInputs")]
        public VdShaderVertexInput[] VertexInputs { get; set; } = [];

        /// <summary>Vertex stage outputs (varyings) - identical to <see cref="FragmentInputs"/> by contract (compile-time rule V5b).</summary>
        [JsonPropertyName("vertexOutputs")]
        public VdShaderVarying[] VertexOutputs { get; set; } = [];

        /// <summary>Fragment stage inputs (varyings).</summary>
        [JsonPropertyName("fragmentInputs")]
        public VdShaderVarying[] FragmentInputs { get; set; } = [];

        /// <summary>Uniform blocks with std140 member placement, merged across stages (one entry per set/binding).</summary>
        [JsonPropertyName("uniformBlocks")]
        public VdShaderUniformBlock[] UniformBlocks { get; set; } = [];
    }

    /// <summary>One compiled vertex input.</summary>
    public sealed class VdShaderVertexInput
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("location")] public uint Location { get; set; }
        /// <summary>The GLSL type as a Veldrid vertex format (vec3 -> Float3, ivec2 -> Int2, ...). Application formats must be component-compatible.</summary>
        [JsonPropertyName("format")] [JsonConverter(typeof(JsonStringEnumConverter<VertexElementFormat>))] public VertexElementFormat Format { get; set; }
    }

    /// <summary>One stage varying (vertex out / fragment in).</summary>
    public sealed class VdShaderVarying
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("location")] public uint Location { get; set; }
        /// <summary>GLSL spelling: vec2, flat ivec2, mat4, float[4] ...</summary>
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("flat")] public bool Flat { get; set; }
    }

    /// <summary>One uniform block as compiled.</summary>
    public sealed class VdShaderUniformBlock
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("set")] public uint Set { get; set; }
        [JsonPropertyName("binding")] public uint Binding { get; set; }
        /// <summary>std140 size of the block in bytes - the C# struct bound to it must have exactly this Marshal.SizeOf.</summary>
        [JsonPropertyName("sizeBytes")] public uint SizeBytes { get; set; }
        [JsonPropertyName("stages")] [JsonConverter(typeof(JsonStringEnumConverter<ShaderStages>))] public ShaderStages Stages { get; set; }
        [JsonPropertyName("members")] public VdShaderUniformMember[] Members { get; set; } = [];
    }

    /// <summary>One uniform block member with the std140 placement the compiler laid out.</summary>
    public sealed class VdShaderUniformMember
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("offsetBytes")] public uint OffsetBytes { get; set; }
        [JsonPropertyName("sizeBytes")] public uint SizeBytes { get; set; }
    }
}