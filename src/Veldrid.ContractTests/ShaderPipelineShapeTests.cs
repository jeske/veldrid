using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>D10: a shape is a C# type whose field names are the GLSL names; Verify is Bind without a device.</summary>
public class ShaderPipelineShapeTests
{
    // ---- the "compiled" bundle a Quad shader would produce ----
    private static VeldridShaderBundle QuadBundle() => new()
    {
        ShaderName = "Quad",
        ResourceLayoutDescriptions =
        [
            new VdShaderResourceLayout { Elements = [new VdShaderResourceElement { Name = "Projection", Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex }] },
            new VdShaderResourceLayout { Elements = [new VdShaderResourceElement { Name = "MainTexture", Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment }, new VdShaderResourceElement { Name = "MainSampler", Kind = ResourceKind.Sampler, Stages = ShaderStages.Fragment }] },
        ],
        Interface = new VdShaderInterface
        {
            VertexInputs = [new VdShaderVertexInput { Name = "Position", Location = 0, Format = VertexElementFormat.Float2 }, new VdShaderVertexInput { Name = "TexCoord", Location = 1, Format = VertexElementFormat.Float2 }, new VdShaderVertexInput { Name = "Color", Location = 2, Format = VertexElementFormat.Float4 }],
            UniformBlocks = [new VdShaderUniformBlock { Name = "Projection", Set = 0, Binding = 0, SizeBytes = 80, Stages = ShaderStages.Vertex,
                Members = [new VdShaderUniformMember { Name = "Matrix", Type = "mat4", OffsetBytes = 0, SizeBytes = 64 }, new VdShaderUniformMember { Name = "Scale", Type = "float", OffsetBytes = 64, SizeBytes = 4 }] }],
        },
    };

    [StructLayout(LayoutKind.Sequential)] public struct QuadVertex { public Vector2 Position; public Vector2 TexCoord; [VertexFormat(VertexElementFormat.Byte4_Norm)] public RgbaByte Color; }
    [StructLayout(LayoutKind.Sequential)] public struct Projection { public Matrix4x4 Matrix; public float Scale; public float _pad0, _pad1, _pad2; }

    [ShaderBundle("Quad")]
    public sealed partial class QuadShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertex> Vertices;
        public UniformBlock<Projection> Projection;
        public TextureSlot MainTexture;
        public SamplerSlot MainSampler;
    }

    [Fact]
    public void GoodShape_Verifies_NoViolations() => Assert.Empty(ShaderPipelineShape.Verify<QuadShape>(QuadBundle()));

    [Fact]
    public void VertexLayout_IsGeneratedFromTheStruct_ByFieldNameOffsetAndFormat()
    {
        var layout = ShapeDescriptorBuilder.VertexLayoutFor(typeof(QuadVertex), perInstance: false);
        Assert.Equal(new[] { "Position", "TexCoord", "Color" }, layout.Elements.Select(e => e.Name).ToArray());
        Assert.Equal(VertexElementFormat.Byte4_Norm, layout.Elements[2].Format);
        Assert.Equal(16u, layout.Elements[2].Offset);
        Assert.Equal(20u, layout.Stride);
    }

    [ShaderBundle("Quad")]
    public sealed partial class MisspelledTextureShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertex> Vertices;
        public UniformBlock<Projection> Projection;
        public TextureSlot MainTex;        // GLSL says MainTexture
        public SamplerSlot MainSampler;
    }

    [Fact]
    public void MisspelledField_IsReportedBothWays()
    {
        var v = ShaderPipelineShape.Verify<MisspelledTextureShape>(QuadBundle());
        Assert.Contains(v, x => x.Rule == ShaderContractVerifier.Rule_V7b_ResourceLayouts && x.Name == "MainTexture" && x.SideA.Contains("no field"));
        Assert.Contains(v, x => x.Rule == ShaderContractVerifier.Rule_V7b_ResourceLayouts && x.Name == "MainTex" && x.SideB.Contains("no such resource"));
    }

    [ShaderBundle("Quad")]
    public sealed partial class WrongKindShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertex> Vertices;
        public UniformBlock<Projection> Projection;
        public SamplerSlot MainTexture;    // kind swapped
        public TextureSlot MainSampler;
    }

    [Fact]
    public void WrongSlotKind_IsReported()
    {
        var v = ShaderPipelineShape.Verify<WrongKindShape>(QuadBundle());
        Assert.Equal(2, v.Count(x => x.Detail.Contains("does not accept")));
    }

    [StructLayout(LayoutKind.Sequential)] public struct ProjectionSwapped { public float Scale; public Matrix4x4 Matrix; public float _pad0, _pad1, _pad2; }
    [ShaderBundle("Quad")]
    public sealed partial class SwappedStructShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertex> Vertices;
        public UniformBlock<ProjectionSwapped> Projection;
        public TextureSlot MainTexture;
        public SamplerSlot MainSampler;
    }

    [Fact]
    public void StructMemberAtWrongOffset_IsReported_V7c()
    {
        var v = ShaderPipelineShape.Verify<SwappedStructShape>(QuadBundle());
        Assert.Contains(v, x => x.Rule == ShaderContractVerifier.Rule_V7c_UniformStructs && x.Name == "Projection.Scale");
        Assert.Contains(v, x => x.Rule == ShaderContractVerifier.Rule_V7c_UniformStructs && x.Name == "Projection.Matrix");
    }

    [StructLayout(LayoutKind.Sequential)] public struct QuadVertexSwapped { public Vector2 TexCoord; public Vector2 Position; [VertexFormat(VertexElementFormat.Byte4_Norm)] public RgbaByte Color; }
    [ShaderBundle("Quad")]
    public sealed partial class SwappedVertexShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertexSwapped> Vertices;
        public UniformBlock<Projection> Projection;
        public TextureSlot MainTexture;
        public SamplerSlot MainSampler;
    }

    [Fact]
    public void VertexFieldOrderMismatch_IsReported_V7a()
    {
        var v = ShaderPipelineShape.Verify<SwappedVertexShape>(QuadBundle());
        Assert.Equal(2, v.Count(x => x.Rule == ShaderContractVerifier.Rule_V7a_VertexInputs && x.Detail.Contains("NAME")));
    }

    [ShaderBundle("NotQuad")]
    public sealed partial class WrongBundleShape : ShaderPipelineShape
    {
        public VertexInput<QuadVertex> Vertices;
        public UniformBlock<Projection> Projection;
        public TextureSlot MainTexture;
        public SamplerSlot MainSampler;
    }

    [Fact]
    public void ShapeNamesADifferentBundle_IsReported()
        => Assert.Contains(ShaderPipelineShape.Verify<WrongBundleShape>(QuadBundle()), x => x.Rule == ShaderContractVerifier.Rule_V7_ShaderSet && x.Detail.Contains("wrong bundle"));

    [Fact]
    public void GlslNameThatIsNotACSharpIdentifier_IsReported()
    {
        var b = QuadBundle(); b.ResourceLayoutDescriptions[1].Elements[0].Name = "class";
        Assert.Contains(ShaderPipelineShape.Verify<QuadShape>(b), x => x.Rule == ShaderContractVerifier.Rule_Identifier && x.Name == "class");
    }

    [Fact]
    public void EnumerateShapes_FindsEveryConcreteShapeWithItsBundle()
    {
        var shapes = ShaderPipelineShape.EnumerateShapes(typeof(ShaderPipelineShapeTests).Assembly);
        Assert.Contains(shapes, s => s.ShapeType == typeof(QuadShape) && s.BundleName == "Quad");
        Assert.Contains(shapes, s => s.ShapeType == typeof(WrongBundleShape) && s.BundleName == "NotQuad");
    }

    [Fact]
    public void BundleFormat_Version1_OrNoInterface_IsRejectedOnDeserialize()
    {
        var ex = Assert.Throws<VeldridException>(() => VeldridShaderBundle.Deserialize("{\"version\":1,\"shaderName\":\"Old\",\"backends\":{}}"));
        Assert.Contains("Recompile", ex.Message);
        Assert.Throws<VeldridException>(() => VeldridShaderBundle.Deserialize("{\"version\":2,\"shaderName\":\"NoItf\",\"backends\":{}}"));
    }

    [Fact]
    public void Bundle_RoundTripsInterface()
    {
        var back = VeldridShaderBundle.Deserialize(QuadBundle().Serialize());
        Assert.Equal(VeldridShaderBundle.CurrentFormatVersion, back.Version);
        Assert.Equal("Color", back.Interface.VertexInputs[2].Name);
        Assert.Equal(64u, back.Interface.UniformBlocks.Single().Members[1].OffsetBytes);
    }
}