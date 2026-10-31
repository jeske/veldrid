using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class ShapeCommandBindingTests
{
    [Fact]
    public void BindingBeforeSelectingPipelineThrows()
    {
        var state = new ShapeCommandBindings();
        Assert.Throws<ShaderContractException>(() => state.RequireOwner(new ImGuiShape()));
        Assert.Throws<ShaderContractException>(() => state.ValidateDraw());
    }

    [Fact]
    public void DifferentShapeInstanceCannotBindToSelectedPipeline()
    {
        var state = new ShapeCommandBindings();
        var selected = new ImGuiShape();
        state.Select(selected, 1, true);
        state.RequireOwner(selected);
        Assert.Throws<ShaderContractException>(() => state.MarkResources(new ImGuiShape()));
        Assert.Throws<ShaderContractException>(() => state.MarkVertex(new ImGuiShape(), 0));
    }

    [Fact]
    public void ResetRemovesOwnership()
    {
        var state = new ShapeCommandBindings();
        var shape = new ImGuiShape();
        state.Select(shape, 1, true);
        state.MarkResources(shape);
        state.MarkVertex(shape, 0);
        state.Reset();
        Assert.Throws<ShaderContractException>(() => state.RequireOwner(shape));
    }

    [Fact]
    public void CannotInventVertexSlotsOrSelectUnverifiedPipeline()
    {
        var state = new ShapeCommandBindings();
        var shape = new ImGuiShape();
        Assert.Throws<ShaderContractException>(() => state.Select(null!, 0, false));
        state.Select(shape, 1, true);
        Assert.Throws<ShaderContractException>(() => state.MarkVertex(shape, 1));
    }
}