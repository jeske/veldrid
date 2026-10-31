using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>Device-free checks of the command-binding contract's ownership rules (GPU behaviour is covered by TypedBindingContractTests).</summary>
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
    public void DifferentShapeInstanceIsNotTheOwner()
    {
        var state = new ShapeCommandBindings();
        var selected = new ImGuiShape();
        state.Select(selected);
        state.RequireOwner(selected);
        Assert.Throws<ShaderContractException>(() => state.RequireOwner(new ImGuiShape()));
    }

    [Fact]
    public void ResetRemovesOwnership()
    {
        var state = new ShapeCommandBindings();
        var shape = new ImGuiShape();
        state.Select(shape);
        state.Reset();
        Assert.Throws<ShaderContractException>(() => state.RequireOwner(shape));
    }

    [Fact]
    public void CannotSelectUnverifiedPipeline()
    {
        var state = new ShapeCommandBindings();
        Assert.Throws<ShaderContractException>(() => state.Select(null!));
    }
}