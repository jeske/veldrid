using System;
using System.Collections.Generic;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class SharedSetZeroAllocationTests
{
    private static ResourceLayoutDescription Layout(string first, string second) => new()
    {
        Elements = new[]
        {
            new ResourceLayoutElementDescription(first, ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription(second, ResourceKind.Sampler, ShaderStages.Fragment),
        },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooleanPass_AllocatesZero_EvenOnRejection(bool mismatch)
    {
        var a = Layout("Image", "Sampler");
        var b = Layout(mismatch ? "Other" : "Image", "Sampler");
        for (int i = 0; i < 1000; i++) ShaderContractVerifier.CheckSharedSetBinding(0, a, b, null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int i = 0; i < 10000; i++) accepted &= ShaderContractVerifier.CheckSharedSetBinding(0, a, b, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(!mismatch, accepted);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void FailureRerun_CollectsEveryMismatch()
    {
        var a = Layout("Image", "Sampler");
        var b = Layout("OtherImage", "OtherSampler");
        Assert.False(ShaderContractVerifier.CheckSharedSetBinding(2, a, b, null));
        var failures = new List<ShaderContractViolation>();
        Assert.False(ShaderContractVerifier.CheckSharedSetBinding(2, a, b, failures));
        Assert.Equal(2, failures.Count);
        Assert.Equal("set 2 binding 0", failures[0].Name);
        Assert.Equal("set 2 binding 1", failures[1].Name);
        Assert.All(failures, f => Assert.Equal("V7d", f.Rule));
    }
}