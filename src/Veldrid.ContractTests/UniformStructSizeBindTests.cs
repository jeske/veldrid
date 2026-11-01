using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     V7c through the THROWING path (<see cref="ShaderPipelineShape.Bind{TShape}"/> on a real device, the embedded ImGui bundle):
///     the Projection block's members end at 72 B and the compiler reports the 16-rounded 80 B. A C# struct that is 72 B - because
///     it declares no Size, or because its Size is smaller than its fields (the CLR silently uses the natural size; it never throws) -
///     must be refused with a <see cref="ShaderContractException"/> before any GPU object exists. Only Size = 80 binds.
/// </summary>
public class UniformStructSizeBindTests
{
    // Natural size 72: no Size declared -> not the 16-rounded block size.
    [StructLayout(LayoutKind.Sequential)]
    public struct ImGuiProjection_NoSize { public Matrix4x4 projection; public int IsClipSpaceYInverted; public int UseLegacyColorSpaceHandling; }

    // Size = 8 is smaller than the fields: the runtime ignores it (Marshal.SizeOf = 72), it does not throw. The verifier must.
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct ImGuiProjection_SizeTooSmall { public Matrix4x4 projection; public int IsClipSpaceYInverted; public int UseLegacyColorSpaceHandling; }

    // Size = 80 = the reported block size: the one declaration that binds.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    public struct ImGuiProjection_Size80 { public Matrix4x4 projection; public int IsClipSpaceYInverted; public int UseLegacyColorSpaceHandling; }

    [ShaderBundle("ImGui")]
    public sealed partial class NoSizeShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        public UniformBlock<ImGuiProjection_NoSize> Projection;
        public SamplerSlot FontSampler;
        public TextureSlot FontTexture;
    }

    [ShaderBundle("ImGui")]
    public sealed partial class SizeTooSmallShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        public UniformBlock<ImGuiProjection_SizeTooSmall> Projection;
        public SamplerSlot FontSampler;
        public TextureSlot FontTexture;
    }

    [ShaderBundle("ImGui")]
    public sealed partial class Size80Shape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        public UniformBlock<ImGuiProjection_Size80> Projection;
        public SamplerSlot FontSampler;
        public TextureSlot FontTexture;
    }

    [Fact]
    public void TheRuntimeDoesNotThrowForAnUndersizedStructLayoutSize_ItUsesTheNaturalSize()
    {
        Assert.Equal(72, Marshal.SizeOf<ImGuiProjection_SizeTooSmall>());
        Assert.Equal(72, Marshal.SizeOf<ImGuiProjection_NoSize>());
        Assert.Equal(80, Marshal.SizeOf<ImGuiProjection_Size80>());
    }

    [Fact]
    public void Bind_StructWithoutSize_72BytesAgainst80ByteBlock_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;   // D3D11-only, same guard as CommandContractGpuTests
        using var device = ShapeTestHarness.CreateDevice(GraphicsBackend.Direct3D11);
        var bundle = ShapeTestHarness.LoadBundle(device.ResourceFactory);
        var ex = Assert.Throws<ShaderContractException>(() => ShaderPipelineShape.Bind<NoSizeShape>(bundle, device.ResourceFactory));
        Assert.Contains(ex.Violations, v => v.Rule == ShaderContractVerifier.Rule_V7c_UniformStructs && v.Name == "Projection" && v.SideA.Contains("72 bytes") && v.SideB.Contains("80 bytes"));
        Assert.Contains("Size = N", ex.Message);
    }

    [Fact]
    public void Bind_StructWithSizeSmallerThanItsFields_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;   // D3D11-only, same guard as CommandContractGpuTests
        using var device = ShapeTestHarness.CreateDevice(GraphicsBackend.Direct3D11);
        var bundle = ShapeTestHarness.LoadBundle(device.ResourceFactory);
        var ex = Assert.Throws<ShaderContractException>(() => ShaderPipelineShape.Bind<SizeTooSmallShape>(bundle, device.ResourceFactory));
        Assert.Contains(ex.Violations, v => v.Rule == ShaderContractVerifier.Rule_V7c_UniformStructs && v.Name == "Projection" && v.SideA.Contains("72 bytes") && v.SideB.Contains("80 bytes"));
    }

    [Fact]
    public void Bind_StructWithSizeEqualToTheRoundedBlock_Succeeds_AndCreateBufferMatches()
    {
        if (!OperatingSystem.IsWindows()) return;   // D3D11-only, same guard as CommandContractGpuTests
        using var device = ShapeTestHarness.CreateDevice(GraphicsBackend.Direct3D11);
        var bundle = ShapeTestHarness.LoadBundle(device.ResourceFactory);
        using var shape = ShaderPipelineShape.Bind<Size80Shape>(bundle, device.ResourceFactory);
        shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);   // 80 B, a legal multiple of 16; would throw on a size mismatch
        foreach (var shader in bundle.Shaders) shader.Dispose();
    }
}