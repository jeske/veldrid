using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class IncompleteLayoutTests
{
    [ShaderBundle("Storage")]
    public sealed partial class StorageShape : ShaderPipelineShape
    {
        public StorageBufferSlot<uint> Items;
    }

    [Fact]
    public void StorageWithoutCompiledBlockLayout_IsMetadata_NotSilentlyAccepted()
    {
        // V10: a bundle compiled before storage-block reflection (no storageBlocks) cannot verify the element struct - METADATA ("recompile"), never a guess.
        var bundle = new VeldridShaderBundle
        {
            ShaderName = "Storage",
            Interface = new VdShaderInterface(),
            ResourceLayoutDescriptions = [new VdShaderResourceLayout
            {
                Elements = [new VdShaderResourceElement { Name = "Items", Kind = ResourceKind.StructuredBufferReadOnly, Stages = ShaderStages.Vertex }],
            }],
        };
        Assert.Contains(ShaderPipelineShape.Verify<StorageShape>(bundle), v => v.Rule == "METADATA" && v.Name == "Items");
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