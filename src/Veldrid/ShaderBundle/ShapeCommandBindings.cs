using System;

namespace Veldrid;

/// <summary>
///     Unconditional command-recording contract (kept separate from debug-only backend validation). It mirrors the backend's REAL
///     binding rules and invents none: a vertex buffer stays bound across a pipeline switch (as on D3D11/Vulkan/Metal) and is
///     accepted for the new pipeline iff its verified vertex TYPE matches the new shape's input at that slot; a resource set stays
///     bound iff it was created from the new pipeline's shape (same instance => same layouts). Only a TYPE mismatch forces a rebind.
/// </summary>
internal sealed class ShapeCommandBindings
{
    private ShaderPipelineShape owner;
    // What is actually bound on the command list, independent of which pipeline is selected (this IS the GPU's state model).
    private Type[] boundVertexTypes = Array.Empty<Type>();
    private ResourceFactory[] boundVertexFactories = Array.Empty<ResourceFactory>();
    private ResourceSet[] boundSets = Array.Empty<ResourceSet>();

    internal void Reset()
    {
        owner = null;
        boundVertexTypes = Array.Empty<Type>();
        boundVertexFactories = Array.Empty<ResourceFactory>();
        boundSets = Array.Empty<ResourceSet>();
    }

    internal void Select(ShaderPipelineShape shape)
    {
        owner = shape ?? throw Failure("pipeline has no verified shape");
    }

    internal void RequireOwner(ShaderPipelineShape shape)
    {
        if (owner is null || !ReferenceEquals(owner, shape))
            throw Failure("binding does not belong to the selected pipeline's shape; select that shape's pipeline first");
    }

    internal void MarkVertex(VertexInput input, ResourceFactory factory)
    {
        // No owner check: vertex bindings are pipeline-independent state; ValidateDraw compares the bound TYPE to the selected shape.
        uint slot = input.SlotIndex;
        if (slot >= boundVertexTypes.Length)
        {
            Array.Resize(ref boundVertexTypes, (int)slot + 1);
            Array.Resize(ref boundVertexFactories, (int)slot + 1);
        }
        boundVertexTypes[slot] = input.VertexType;
        boundVertexFactories[slot] = factory;
    }

    internal void MarkResourceSet(ResourceSet set)
    {
        if (owner is null) throw Failure("no verified pipeline selected; select the pipeline before binding its resource sets");
        ThrowIfSetRefused(owner, set, set.VerifiedSetIndex);
        uint index = set.VerifiedSetIndex;
        if (index >= boundSets.Length) Array.Resize(ref boundSets, (int)index + 1);
        boundSets[index] = set;
    }

    internal void ValidateDraw()
    {
        if (owner is null) throw Failure("no verified pipeline selected");
        owner.EnsureBound();
        for (uint slot = 0; slot < owner.VertexInputCount; slot++)
        {
            Type expected = owner.VertexTypeAt(slot);
            Type bound = slot < boundVertexTypes.Length ? boundVertexTypes[slot] : null;
            if (bound is null) throw Failure($"vertex input {slot} ({expected?.Name}) has no buffer bound");
            if (bound != expected) throw Failure($"vertex input {slot} expects {expected?.Name} but a {bound.Name} buffer is bound; bind the matching typed buffer");
            if (!ReferenceEquals(boundVertexFactories[slot], owner.BoundFactory)) throw Failure($"vertex input {slot} buffer belongs to a different device");
        }
        for (uint set = 0; set < owner.SetCount; set++)
        {
            ResourceSet bound = set < boundSets.Length ? boundSets[set] : null;
            if (bound is null) throw Failure($"resource set {set} is not bound for this pipeline");
            ThrowIfSetRefused(owner, bound, set);
            if (bound.IsDisposed) throw Failure($"resource set {set} is disposed");
        }
    }

    // The refusal carries the verifier's per-binding violations (V7d names the binding and both bundles' registers), not a summary.
    private static void ThrowIfSetRefused(ShaderPipelineShape owner, ResourceSet set, uint index)
    {
        var violations = owner.ResourceSetBindingViolations(set, index);
        if (violations.Count == 0) return;
        violations.Insert(0, new ShaderContractViolation("BIND", $"set {index}", $"created for {set.VerifiedShape?.GetType().Name ?? "(no shape)"}", $"selected pipeline {owner.GetType().Name}", "a set binds only to a pipeline whose shader declares that set identically (layout and flat registers)"));
        throw new ShaderContractException(owner.BundleName, violations);
    }

    private static ShaderContractException Failure(string detail) => new("command bindings",
        [new ShaderContractViolation("BIND", "pipeline/shape", "command recording", "verified shape bindings required", detail)]);
}