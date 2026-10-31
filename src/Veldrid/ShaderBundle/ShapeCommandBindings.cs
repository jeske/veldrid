using System;

namespace Veldrid;

internal readonly record struct ShapeResourceBindingRevision(ulong Value);

/// <summary>Unconditional command-recording contract. Kept separate from debug-only backend validation.</summary>
internal sealed class ShapeCommandBindings
{
    private ShaderPipelineShape owner;
    private bool resourcesReady;
    private bool[] verticesReady = Array.Empty<bool>();
    private ShapeResourceBindingRevision boundRevision;

    internal void Reset()
    {
        owner = null;
        resourcesReady = false;
        verticesReady = Array.Empty<bool>();
    }

    internal void Select(ShaderPipelineShape shape, int vertexCount, bool hasResources)
    {
        owner = shape ?? throw Failure("pipeline has no verified shape");
        verticesReady = new bool[vertexCount];
        resourcesReady = !hasResources;
        boundRevision = shape.ResourceBindingRevision;
    }

    internal void RequireOwner(ShaderPipelineShape shape)
    {
        if (owner is null || !ReferenceEquals(owner, shape))
            throw Failure("binding shape does not own the selected pipeline; select its pipeline first");
    }

    internal void MarkResources(ShaderPipelineShape shape)
    {
        RequireOwner(shape);
        resourcesReady = true;
        boundRevision = shape.ResourceBindingRevision;
    }

    internal void MarkVertex(ShaderPipelineShape shape, uint slot)
    {
        RequireOwner(shape);
        if (slot >= verticesReady.Length) throw Failure("vertex input does not exist in the selected pipeline");
        verticesReady[slot] = true;
    }

    internal void ValidateDraw()
    {
        if (owner is null) throw Failure("no verified graphics pipeline selected");
        owner.EnsureBound();
        if (!resourcesReady) throw Failure("resource sets must be rebound after selecting a pipeline");
        if (boundRevision != owner.ResourceBindingRevision)
            throw Failure("resource slots changed after binding; call BindResourceSets again before drawing or dispatching");
        for (int i = 0; i < verticesReady.Length; i++)
            if (!verticesReady[i]) throw Failure($"vertex input {i} must be rebound through its named slot");
    }

    private static ShaderContractException Failure(string detail) => new("command bindings",
        [new ShaderContractViolation("BIND", "pipeline/shape", "command recording", "verified shape bindings required", detail)]);
}