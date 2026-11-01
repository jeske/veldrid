using System.Collections.Generic;
using System.Linq;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     V7d (<see cref="ShaderContractVerifier.VerifySharedSetBinding"/>): ONE ResourceSet bound across pipelines compiled from DIFFERENT
///     bundles (the mesh material set: GBufferMesh / GBufferInstanced / ShadowDepth / ShadowDepthInstanced). Beyond identical declarations,
///     the two bundles' FLAT binding maps for that set must agree - the absolute D3D11 register / Metal index each cross-compiled shader
///     reads. "Declares 1..6, reads 1 and 6, the compiler flattened 6 to slot 2" is the bug class: the set would put resource 6 where this
///     shader never looks. A difference is surfaced, never remapped around.
/// </summary>
public class SharedResourceSetBindingTests
{
    // The material set as GBuffer.frag declares it: four textures, a sampler, a uniform block at set 1.
    private static ResourceLayoutDescription MaterialLayout() => new ResourceLayoutDescriptionBuilder()
        .Add("AlbedoTexture", ResourceKind.TextureReadOnly).Add("NormalTexture", ResourceKind.TextureReadOnly)
        .Add("SpecularTexture", ResourceKind.TextureReadOnly).Add("EmissiveTexture", ResourceKind.TextureReadOnly)
        .Add("MaterialSampler", ResourceKind.Sampler).Add("MaterialParams", ResourceKind.UniformBuffer).Build();

    // The flat map a deterministic compiler assigns to that declaration: textures t0..t3, sampler s0, constant buffer b1 (b0 is set 0's).
    private static VdShaderBindingEntry[] MaterialFlatMap() =>
    [
        Entry(1, 0, ResourceKind.TextureReadOnly, 0), Entry(1, 1, ResourceKind.TextureReadOnly, 1), Entry(1, 2, ResourceKind.TextureReadOnly, 2),
        Entry(1, 3, ResourceKind.TextureReadOnly, 3), Entry(1, 4, ResourceKind.Sampler, 0), Entry(1, 5, ResourceKind.UniformBuffer, 1),
    ];

    private static VdShaderBindingEntry Entry(uint set, uint binding, ResourceKind kind, uint flat)
        => new() { Set = set, Binding = binding, Kind = kind, Stages = ShaderStages.Fragment, FlatIndex = flat };

    [Fact]
    public void IdenticalDeclarationsAndFlatMaps_AreAccepted()
    {
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, MaterialLayout(), MaterialFlatMap(), MaterialLayout(), MaterialFlatMap(), violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void SameDeclarations_ButTheCompilerFlattenedOneBindingDifferently_IsRefused_NamingTheBinding()
    {
        // The pipeline's shader declares all six but reads only Albedo and Emissive; its compiler put Emissive at t1 instead of t3.
        var pipelineMap = MaterialFlatMap();
        pipelineMap.Single(e => e.Binding == 3).FlatIndex = 1;
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, MaterialLayout(), pipelineMap, MaterialLayout(), MaterialFlatMap(), violations);
        var v = Assert.Single(violations);
        Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule);
        Assert.Equal("EmissiveTexture", v.Name);
        Assert.Contains("flat 1", v.SideA);
        Assert.Contains("flat 3", v.SideB);
    }

    [Fact]
    public void SameDeclarations_ButOneBundleHasNoFlatEntryForABinding_IsRefused()
    {
        // A compiler that ELIDED a declared-but-unread resource produces no flat entry for it (contract rule D3 forbids this; V7d sees it).
        var pipelineMap = MaterialFlatMap().Where(e => e.Binding != 1).ToArray();
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, MaterialLayout(), pipelineMap, MaterialLayout(), MaterialFlatMap(), violations);
        var v = Assert.Single(violations);
        Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule);
        Assert.Equal("NormalTexture", v.Name);
        Assert.Contains("no flat binding entry", v.SideA);
    }

    [Fact]
    public void ShaderThatDeclaresOnlyPartOfTheSet_IsRefused()
    {
        // ShadowDepth.frag before 2026-11-01: AlbedoTexture at 0 and MaterialSampler at 4, bindings 1-3 and 5 undeclared.
        var partial = new ResourceLayoutDescriptionBuilder()
            .Add("AlbedoTexture", ResourceKind.TextureReadOnly).Add(null, ResourceKind.TextureReadOnly).Add(null, ResourceKind.TextureReadOnly)
            .Add(null, ResourceKind.TextureReadOnly).Add("MaterialSampler", ResourceKind.Sampler).Build();
        var partialMap = new[] { Entry(1, 0, ResourceKind.TextureReadOnly, 0), Entry(1, 4, ResourceKind.Sampler, 0) };
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, partial, partialMap, MaterialLayout(), MaterialFlatMap(), violations);
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule));
    }

    [Fact]
    public void BothBundlesWithoutFlatMaps_VulkanOnlyStyle_CompareDeclarationsOnly()
    {
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, MaterialLayout(), null, MaterialLayout(), null, violations);
        Assert.Empty(violations);
    }

    /// <summary>Builds the compiled form of a set's layout: elements indexed by binding, unused bindings nameless (as the bundle stores them).</summary>
    private sealed class ResourceLayoutDescriptionBuilder
    {
        private readonly List<ResourceLayoutElementDescription> elements = new();
        public ResourceLayoutDescriptionBuilder Add(string name, ResourceKind kind)
        {
            elements.Add(name is null ? default : new ResourceLayoutElementDescription(name, kind, ShaderStages.Fragment));
            return this;
        }
        public ResourceLayoutDescription Build() => new() { Elements = elements.ToArray() };
    }
}