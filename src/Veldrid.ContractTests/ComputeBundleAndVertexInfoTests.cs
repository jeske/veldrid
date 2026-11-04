using System;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     AN_Monsters spec 204 (Q2): the bundle surface a COMPUTE bundle needs (<see cref="VeldridShaderBundle.IsCompute"/>,
///     <see cref="VeldridShaderBundle.GetComputeShaderData"/>) and the V11 <see cref="VdShaderInterface.HlslVertexInfoRegister"/> field
///     round-trip through the .vdshader JSON. The GPU proof of V11 (gl_InstanceIndex includes instanceStart on D3D11) is
///     AN_Monsters <c>cmd\viewcards.bat --spike indirect --backend both</c> once Veldrid.SPIRV emits the cbuffer against this Veldrid.
/// </summary>
public class ComputeBundleAndVertexInfoTests
{
    private static VeldridShaderBundle ComputeBundle() => new()
    {
        ShaderName = "Fill",
        ComputeSource = "fill.comp.glsl",
        Interface = new VdShaderInterface(),
        ResourceLayoutDescriptions = [],
        Backends =
        {
            [VeldridShaderBundle.GetBackendKey(GraphicsBackend.Vulkan)] = new VdShaderBackendData { ShaderFormat = "spirv", ComputeEntryPoint = "main", ComputeShaderData = Convert.ToBase64String(new byte[] { 3, 2, 35, 7 }) },
            [VeldridShaderBundle.GetBackendKey(GraphicsBackend.Direct3D11)] = new VdShaderBackendData { ShaderFormat = "dxbc", ComputeEntryPoint = "main", ComputeShaderData = Convert.ToBase64String(new byte[] { 68, 88, 66, 67 }) },
        },
    };

    private static VeldridShaderBundle GraphicsBundle(uint? vertexInfoRegister) => new()
    {
        ShaderName = "Quad",
        Interface = new VdShaderInterface { HlslVertexInfoRegister = vertexInfoRegister },
        ResourceLayoutDescriptions = [],
        Backends =
        {
            [VeldridShaderBundle.GetBackendKey(GraphicsBackend.Direct3D11)] = new VdShaderBackendData { ShaderFormat = "dxbc", VertexEntryPoint = "main", FragmentEntryPoint = "main", VertexShaderData = Convert.ToBase64String(new byte[] { 1 }), FragmentShaderData = Convert.ToBase64String(new byte[] { 2 }) },
        },
    };

    [Fact]
    public void ComputeBundle_IsCompute_AndYieldsItsBytesAndEntryPoint()
    {
        var bundle = ComputeBundle();
        Assert.True(bundle.IsCompute);
        var (bytes, entry) = bundle.GetComputeShaderData(GraphicsBackend.Vulkan);
        Assert.Equal(new byte[] { 3, 2, 35, 7 }, bytes);
        Assert.Equal("main", entry);
        Assert.Throws<VeldridException>(() => bundle.GetComputeShaderData(GraphicsBackend.Metal));   // no data for that backend
    }

    [Fact]
    public void GraphicsBundle_IsNotCompute()
    {
        Assert.False(GraphicsBundle(null).IsCompute);
        Assert.Throws<VeldridException>(() => GraphicsBundle(null).GetComputeShaderData(GraphicsBackend.Direct3D11));
    }

    [Fact]
    public void ComputeBundle_SurvivesSerialization_AndStaysCompute()
    {
        var roundTripped = VeldridShaderBundle.Deserialize(ComputeBundle().Serialize());
        Assert.True(roundTripped.IsCompute);
        Assert.Equal("fill.comp.glsl", roundTripped.ComputeSource);
        Assert.Null(roundTripped.Interface.HlslVertexInfoRegister);
    }

    [Fact]
    public void HlslVertexInfoRegister_RoundTripsThroughJson_AndIsOmittedWhenNull()
    {
        string withRegister = GraphicsBundle(2).Serialize();
        Assert.Contains("\"hlslVertexInfoRegister\": 2", withRegister.Replace("\"hlslVertexInfoRegister\":2", "\"hlslVertexInfoRegister\": 2"));
        Assert.Equal(2u, VeldridShaderBundle.Deserialize(withRegister).Interface.HlslVertexInfoRegister);

        string without = GraphicsBundle(null).Serialize();
        Assert.DoesNotContain("hlslVertexInfoRegister", without);
        Assert.Null(VeldridShaderBundle.Deserialize(without).Interface.HlslVertexInfoRegister);
    }

    [Fact]
    public void D3D11_ReportsNoIndirectBaseInstance_ButDirectBaseInstance()
    {
        // V11 / T3: a GPU-authored indirect record's FirstInstance is not shader-visible on D3D11; direct draws are (the host fills the cbuffer).
        GraphicsDevice device;
        try { device = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions()); }
        catch (Exception) { return; }   // no D3D11 device here: nothing to assert
        using (device)
        {
            Assert.False(device.Features.DrawIndirectBaseInstance);
            Assert.True(device.Features.DrawBaseInstance);
            Assert.True(device.Features.DrawIndirect);
        }
    }
}