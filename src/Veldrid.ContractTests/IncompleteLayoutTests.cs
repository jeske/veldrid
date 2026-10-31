using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class IncompleteLayoutTests
{
    [ShaderBundle("Storage")]
    public sealed partial class StorageShape : ShaderPipelineShape
    {
        public StorageBufferSlot Items;
    }

    [Fact]
    public void StorageCannotPassOnKindOnlyMetadata()
    {
        var bundle = new VeldridShaderBundle
        {
            ShaderName = "Storage",
            Interface = new VdShaderInterface(),
            ResourceLayoutDescriptions = [new VdShaderResourceLayout
            {
                Elements = [new VdShaderResourceElement { Name = "Items", Kind = ResourceKind.StructuredBufferReadOnly, Stages = ShaderStages.Vertex }],
            }],
        };
        Assert.Contains(ShaderPipelineShape.Verify<StorageShape>(bundle), v => v.Rule == "UNSUPPORTED_LAYOUT" && v.Name == "Items");
    }

    [Fact]
    public void UniformResourceWithoutMemberMetadataIsNotSilentlySkipped()
    {
        var bundle = new VeldridShaderBundle
        {
            ShaderName = "ImGui",
            Interface = new VdShaderInterface(),
            ResourceLayoutDescriptions = [new VdShaderResourceLayout
            {
                Elements = [new VdShaderResourceElement { Name = "Projection", Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex }],
            }],
        };
        Assert.Contains(ShaderPipelineShape.Verify<ImGuiShape>(bundle), v => v.Rule == "METADATA" && v.Name == "Projection");
    }
}