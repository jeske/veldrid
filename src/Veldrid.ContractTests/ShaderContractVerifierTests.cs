using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>Pipeline-creation half of the shader interface contract (V7a/V7b/V7c), pure over descriptions - no device.</summary>
[Collection("ShaderUniformBlockRegistry (global state)")]
public class ShaderContractVerifierTests
{
    // A ResourceLayout that exists only to carry a description (the real ones need a device).
    private sealed class DescriptionOnlyResourceLayout(ResourceLayoutDescription description) : ResourceLayout(ref description)
    {
        public override bool IsDisposed => false;
        public override string Name { get; set; } = "test";
        public override void Dispose() { }
    }

    private static PrecompiledShaderResult Bundle(string name, VdShaderInterface itf, params ResourceLayoutDescription[] layouts)
        => new(Array.Empty<Shader>(), layouts, null, itf, name);

    private static VdShaderInterface InterfaceWith(params VdShaderVertexInput[] inputs) => new() { VertexInputs = inputs };
    private static VdShaderVertexInput In(string name, uint location, VertexElementFormat format) => new() { Name = name, Location = location, Format = format };

    private static GraphicsPipelineDescription Desc(VertexLayoutDescription[] vertexLayouts, params ResourceLayout[] resourceLayouts)
        => new()
        {
            ShaderSet = new ShaderSetDescription(vertexLayouts, Array.Empty<Shader>()),
            ResourceLayouts = resourceLayouts,
        };

    [Fact]
    public void V7a_MatchingNamesAndCompatibleFormats_NoViolations()
    {
        var bundle = Bundle("Quad", InterfaceWith(In("Position", 0, VertexElementFormat.Float3), In("Color", 1, VertexElementFormat.Float4)));
        var desc = Desc([new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Byte4_Norm))]);   // normalised bytes feed a vec4
        Assert.Empty(ShaderContractVerifier.Verify(ref desc, bundle));
    }

    [Fact]
    public void V7a_RenamedElement_IsNamedInTheViolation()
    {
        var bundle = Bundle("Quad", InterfaceWith(In("Position", 0, VertexElementFormat.Float3)));
        var desc = Desc([new VertexLayoutDescription(new VertexElementDescription("vsin_Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3))]);
        var v = Assert.Single(ShaderContractVerifier.Verify(ref desc, bundle));
        Assert.Equal(ShaderContractVerifier.Rule_V7a_VertexInputs, v.Rule);
        Assert.Equal("Position", v.Name);
        Assert.Contains("vsin_Position", v.SideA);
    }

    [Fact]
    public void V7a_LocationsRunAcrossLayouts_AndMissingOrExtraElementsAreReported()
    {
        var bundle = Bundle("Inst", InterfaceWith(In("Position", 0, VertexElementFormat.Float3), In("InstanceOffset", 1, VertexElementFormat.Float3), In("InstanceTint", 2, VertexElementFormat.Float4)));
        var desc = Desc([
            new VertexLayoutDescription(new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)),
            new VertexLayoutDescription(new VertexElementDescription("InstanceOffset", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)) { InstanceStepRate = 1 }]);
        var v = Assert.Single(ShaderContractVerifier.Verify(ref desc, bundle));
        Assert.Equal("InstanceTint", v.Name);
        Assert.Contains("do not provide", v.Detail);
    }

    [Theory]
    [InlineData(VertexElementFormat.Float3, VertexElementFormat.Float3, true)]
    [InlineData(VertexElementFormat.Byte4_Norm, VertexElementFormat.Float4, true)]
    [InlineData(VertexElementFormat.Half2, VertexElementFormat.Float2, true)]
    [InlineData(VertexElementFormat.Byte4, VertexElementFormat.Float4, false)]     // unnormalised integer into a float vec4
    [InlineData(VertexElementFormat.Int2, VertexElementFormat.Int2, true)]
    [InlineData(VertexElementFormat.Float2, VertexElementFormat.Float3, false)]
    public void V7a_FormatCompatibility(VertexElementFormat pipeline, VertexElementFormat glsl, bool compatible)
        => Assert.Equal(compatible, ShaderContractVerifier.FormatsCompatible(pipeline, glsl));

    [Fact]
    public void V7b_ResourceElementRenamedAtSameSlot_Throws_ExtraUnusedElementTolerated()
    {
        var compiled = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ProjectionBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("AtlasTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment));
        var bundle = Bundle("Tiles", new VdShaderInterface(), compiled);
        var pipelineLayout = new DescriptionOnlyResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ProjectionBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("Atlas", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("UnusedSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        var desc = Desc([], pipelineLayout);
        var v = Assert.Single(ShaderContractVerifier.Verify(ref desc, bundle));
        Assert.Equal(ShaderContractVerifier.Rule_V7b_ResourceLayouts, v.Rule);
        Assert.Equal("AtlasTexture", v.Name);
    }

    [StructLayout(LayoutKind.Sequential)]
    [VdShaderUniformBlock("Tiles", "ChunkOriginBuffer")]
    private struct ChunkOriginGood { public float ChunkOriginWorldPixelX; public float ChunkOriginWorldPixelY; public Vector2 Padding; }

    [StructLayout(LayoutKind.Sequential)]
    [VdShaderUniformBlock("Tiles", "ChunkOriginBuffer")]
    private struct ChunkOriginSwapped { public float ChunkOriginWorldPixelY; public float ChunkOriginWorldPixelX; public Vector2 Padding; }

    private static VdShaderUniformBlock ChunkOriginBlock() => new()
    {
        Name = "ChunkOriginBuffer", Set = 1, Binding = 0, SizeBytes = 16, Stages = ShaderStages.Vertex,
        Members =
        [
            new VdShaderUniformMember { Name = "ChunkOriginWorldPixelX", Type = "float", OffsetBytes = 0, SizeBytes = 4 },
            new VdShaderUniformMember { Name = "ChunkOriginWorldPixelY", Type = "float", OffsetBytes = 4, SizeBytes = 4 },
            new VdShaderUniformMember { Name = "Padding", Type = "vec2", OffsetBytes = 8, SizeBytes = 8 },
        ],
    };

    [Fact]
    public void V7c_StructFieldsMatchByNameAndOffset_Passes_SwappedFieldsFail()
    {
        ShaderUniformBlockRegistry.Clear();
        try
        {
            var bundle = Bundle("Tiles", new VdShaderInterface { UniformBlocks = [ChunkOriginBlock()] });
            var desc = Desc([]);

            ShaderUniformBlockRegistry.Register(typeof(ChunkOriginGood), "Tiles", "ChunkOriginBuffer");
            Assert.Empty(ShaderContractVerifier.Verify(ref desc, bundle));

            ShaderUniformBlockRegistry.Clear();
            ShaderUniformBlockRegistry.Register(typeof(ChunkOriginSwapped), "Tiles", "ChunkOriginBuffer");
            var violations = ShaderContractVerifier.Verify(ref desc, bundle);
            Assert.Equal(2, violations.Count);   // both floats sit at the other's offset; names still exist on both sides
            Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7c_UniformStructs, v.Rule));
            Assert.Contains(violations, v => v.Name == "ChunkOriginBuffer.ChunkOriginWorldPixelX");
        }
        finally { ShaderUniformBlockRegistry.Clear(); }
    }

    [Fact]
    public void V7c_RegisterAssembly_FindsAttributedStructs()
    {
        ShaderUniformBlockRegistry.Clear();
        try
        {
            ShaderUniformBlockRegistry.Register(typeof(ShaderContractVerifierTests).Assembly);
            var registered = ShaderUniformBlockRegistry.For("Tiles");
            Assert.Contains(registered, r => r.StructType == typeof(ChunkOriginGood) && r.GlslBlockName == "ChunkOriginBuffer");
        }
        finally { ShaderUniformBlockRegistry.Clear(); }
    }

    [Fact]
    public void ThrowIfViolated_IsNoOp_WhenNoShaderCameFromABundle()
    {
        var desc = Desc([new VertexLayoutDescription(new VertexElementDescription("Anything", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float1))]);
        ShaderContractVerifier.ThrowIfViolated(ref desc);   // no registered shaders -> nothing to verify against
    }

    [Fact]
    public void BundleFormat_Version1_OrNoInterface_IsRejectedOnDeserialize()
    {
        var ex = Assert.Throws<VeldridException>(() => VeldridShaderBundle.Deserialize("{\"version\":1,\"shaderName\":\"Old\",\"backends\":{}}"));
        Assert.Contains("format version 1", ex.Message);
        Assert.Contains("Recompile", ex.Message);
        var ex2 = Assert.Throws<VeldridException>(() => VeldridShaderBundle.Deserialize("{\"version\":2,\"shaderName\":\"NoItf\",\"backends\":{}}"));
        Assert.Contains("without an interface", ex2.Message);
    }

    [Fact]
    public void BundleFormat_CurrentBundle_RoundTripsInterface()
    {
        var bundle = new VeldridShaderBundle { ShaderName = "RT", Interface = new VdShaderInterface { VertexInputs = [In("Position", 0, VertexElementFormat.Float3)], UniformBlocks = [ChunkOriginBlock()] } };
        var back = VeldridShaderBundle.Deserialize(bundle.Serialize());
        Assert.Equal(VeldridShaderBundle.CurrentFormatVersion, back.Version);
        Assert.Equal("Position", back.Interface.VertexInputs.Single().Name);
        Assert.Equal(VertexElementFormat.Float3, back.Interface.VertexInputs.Single().Format);
        Assert.Equal(8u, back.Interface.UniformBlocks.Single().Members[2].OffsetBytes);
    }
}
