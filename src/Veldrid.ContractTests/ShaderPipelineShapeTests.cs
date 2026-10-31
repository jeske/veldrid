using System;
using System.Linq;
using System.Runtime.InteropServices;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>S7: pipeline shapes verified against bundles at TEST time - no device, no window.</summary>
public class ShaderPipelineShapeTests
{
    private static VeldridShaderBundle QuadBundle() => new()
    {
        ShaderName = "Quad",
        ResourceLayoutDescriptions =
        [
            new VdShaderResourceLayout { Elements = [new VdShaderResourceElement { Name = "Projection", Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex }] },
            new VdShaderResourceLayout { Elements = [new VdShaderResourceElement { Name = "Tex", Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment }, new VdShaderResourceElement { Name = "Smp", Kind = ResourceKind.Sampler, Stages = ShaderStages.Fragment }] },
        ],
        Interface = new VdShaderInterface
        {
            VertexInputs = [new VdShaderVertexInput { Name = "Position", Location = 0, Format = VertexElementFormat.Float2 }, new VdShaderVertexInput { Name = "TexCoord", Location = 1, Format = VertexElementFormat.Float2 }],
            UniformBlocks = [new VdShaderUniformBlock { Name = "Projection", Set = 0, Binding = 0, SizeBytes = 64, Stages = ShaderStages.Vertex, Members = [new VdShaderUniformMember { Name = "Matrix", Type = "mat4", OffsetBytes = 0, SizeBytes = 64 }] }],
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    [VdShaderUniformBlock("Quad", "Projection")]
    private struct ProjectionGood { public System.Numerics.Matrix4x4 Matrix; }

    [StructLayout(LayoutKind.Sequential)]
    [VdShaderUniformBlock("Quad", "Projection")]
    private struct ProjectionWrongName { public System.Numerics.Matrix4x4 Proj; }

    private static readonly ResourceLayoutDescription Set0 = new(new ResourceLayoutElementDescription("Projection", ResourceKind.UniformBuffer, ShaderStages.Vertex));
    private static readonly ResourceLayoutDescription Set1 = new(new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment), new ResourceLayoutElementDescription("Smp", ResourceKind.Sampler, ShaderStages.Fragment));
    private static readonly VertexLayoutDescription Verts = new(
        new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
        new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

    [VdShaderPipelineContract]
    public static ShaderPipelineShape GoodShape() => new("Quad", [Verts], [Set0, Set1], [typeof(ProjectionGood)]);

    [Fact]
    public void MatchingShape_NoViolations() => Assert.Empty(ShaderContractVerifier.Verify(GoodShape(), QuadBundle()));

    [Fact]
    public void RenamedVertexElement_V7a()
    {
        var verts = new VertexLayoutDescription(new VertexElementDescription("Pos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2), new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
        var v = Assert.Single(ShaderContractVerifier.Verify(new ShaderPipelineShape("Quad", [verts], [Set0, Set1]), QuadBundle()));
        Assert.Equal(ShaderContractVerifier.Rule_V7a_VertexInputs, v.Rule);
        Assert.Equal("Position", v.Name);
    }

    [Fact]
    public void SwappedTextureAndSampler_V7b_BothSlotsNamed()
    {
        var set1 = new ResourceLayoutDescription(new ResourceLayoutElementDescription("Smp", ResourceKind.Sampler, ShaderStages.Fragment), new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment));
        var violations = ShaderContractVerifier.Verify(new ShaderPipelineShape("Quad", [Verts], [Set0, set1]), QuadBundle());
        Assert.Equal(4, violations.Count);   // name + kind at binding 0, name + kind at binding 1
        Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7b_ResourceLayouts, v.Rule));
    }

    [Fact]
    public void StructOnShape_WrongFieldName_V7c()
    {
        var violations = ShaderContractVerifier.Verify(new ShaderPipelineShape("Quad", [Verts], [Set0, Set1], [typeof(ProjectionWrongName)]), QuadBundle());
        Assert.Equal(2, violations.Count);   // C# field 'Proj' has no member; member 'Matrix' has no field
        Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7c_UniformStructs, v.Rule));
    }

    [Fact]
    public void ShapeNamesOneBundle_ResolverReturnsAnother_V7()
    {
        var other = QuadBundle(); other.ShaderName = "NotQuad";
        var v = Assert.Single(ShaderContractVerifier.Verify(GoodShape(), other));
        Assert.Equal(ShaderContractVerifier.Rule_V7_ShaderSet, v.Rule);
    }

    [Fact]
    public void Registry_EnumeratesAttributedShapes_AndVerifyAllIsCleanForThisAssembly()
    {
        var shapes = ShaderPipelineContractRegistry.Enumerate(typeof(ShaderPipelineShapeTests).Assembly);
        Assert.Contains(shapes, s => s.Shape.BundleName == "Quad" && s.DeclaredBy.EndsWith(nameof(GoodShape)));
        var lines = ShaderPipelineContractRegistry.VerifyAll(typeof(ShaderPipelineShapeTests).Assembly, name => name == "Quad" ? QuadBundle() : throw new InvalidOperationException("unknown bundle " + name));
        Assert.Empty(lines);
        Assert.Equal(["Other"], ShaderPipelineContractRegistry.BundlesWithoutShapes(typeof(ShaderPipelineShapeTests).Assembly, ["Quad", "Other"]));
    }

    [Fact]
    public void ToGraphicsPipelineDescription_UsesTheShapesLayouts()
    {
        var desc = GoodShape().ToGraphicsPipelineDescription(Array.Empty<Shader>(), new ResourceLayout[2], BlendStateDescription.SingleOverrideBlend, new DepthStencilStateDescription(false, false, ComparisonKind.Always), RasterizerStateDescription.DEFAULT, PrimitiveTopology.TriangleList, new OutputDescription());
        Assert.Single(desc.ShaderSet.VertexLayouts);
        Assert.Equal(2, desc.ResourceLayouts.Length);
        Assert.Throws<VeldridException>(() => GoodShape().ToGraphicsPipelineDescription(Array.Empty<Shader>(), new ResourceLayout[1], BlendStateDescription.SingleOverrideBlend, new DepthStencilStateDescription(false, false, ComparisonKind.Always), RasterizerStateDescription.DEFAULT, PrimitiveTopology.TriangleList, new OutputDescription()));
    }
}