using System.Collections.Generic;
using System.Linq;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     V7d (<see cref="ShaderContractVerifier.VerifySharedSetBinding"/>): ONE ResourceSet bound across pipelines compiled from DIFFERENT
///     bundles (the mesh material set: GBufferMesh / GBufferInstanced / ShadowDepth / ShadowDepthInstanced). The rule is IDENTICAL
///     DECLARATIONS of that set - names, kinds, stages, options, including which bindings are unused (Vulkan's descriptor-set-layout
///     compatibility). The register each compiler gave a binding is NOT part of the rule: D3D11 / Metal translate (set, binding) through
///     the PIPELINE's layout at bind time (AN_VeldridSpirv SharedSetCrossPipelineBindingGpuTests proves it on a device).
/// </summary>
public class SharedResourceSetBindingTests
{
    // The material set as GBuffer.frag declares it: four textures, a sampler, a uniform block at set 1.
    private static ResourceLayoutDescription MaterialLayout() => new ResourceLayoutDescriptionBuilder()
        .Add("AlbedoTexture", ResourceKind.TextureReadOnly).Add("NormalTexture", ResourceKind.TextureReadOnly)
        .Add("SpecularTexture", ResourceKind.TextureReadOnly).Add("EmissiveTexture", ResourceKind.TextureReadOnly)
        .Add("MaterialSampler", ResourceKind.Sampler).Add("MaterialParams", ResourceKind.UniformBuffer).Build();

    [Fact]
    public void IdenticalDeclarations_AreAccepted()
    {
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, MaterialLayout(), MaterialLayout(), violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void ShaderThatDeclaresOnlyPartOfTheSet_IsRefused()
    {
        // ShadowDepth.frag before 2026-11-01: AlbedoTexture at 0 and MaterialSampler at 4, bindings 1-3 and 5 undeclared.
        var partial = new ResourceLayoutDescriptionBuilder()
            .Add("AlbedoTexture", ResourceKind.TextureReadOnly).Add(null, ResourceKind.TextureReadOnly).Add(null, ResourceKind.TextureReadOnly)
            .Add(null, ResourceKind.TextureReadOnly).Add("MaterialSampler", ResourceKind.Sampler).Build();
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, partial, MaterialLayout(), violations);
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule));
    }

    [Fact]
    public void SameBindingCount_DifferentNameAtOneBinding_IsRefused_NamingTheBinding()
    {
        var renamed = new ResourceLayoutDescriptionBuilder()
            .Add("AlbedoTexture", ResourceKind.TextureReadOnly).Add("NormalTexture", ResourceKind.TextureReadOnly)
            .Add("SpecularTexture", ResourceKind.TextureReadOnly).Add("GlowTexture", ResourceKind.TextureReadOnly)
            .Add("MaterialSampler", ResourceKind.Sampler).Add("MaterialParams", ResourceKind.UniformBuffer).Build();
        var violations = new List<ShaderContractViolation>();
        ShaderContractVerifier.VerifySharedSetBinding(1, renamed, MaterialLayout(), violations);
        var v = Assert.Single(violations);
        Assert.Equal(ShaderContractVerifier.Rule_V7d_SharedSetBinding, v.Rule);
        Assert.Equal("set 1 binding 3", v.Name);
        Assert.Contains("GlowTexture", v.SideA);
        Assert.Contains("EmissiveTexture", v.SideB);
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