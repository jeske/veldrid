using System;
using System.IO;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     BufferDescription.AllowUpdateBufferOutsideCommandList is the caller's explicit acknowledgement that a
///     GraphicsDevice.UpdateBufferOutsideCommandList write is NOT ordered relative to recorded commands.
///     Default off; checked unconditionally; orthogonal to memory class (Dynamic/Staging grant nothing).
/// </summary>
public sealed class UpdateBufferOutsideCommandListPermissionTests
{
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.VertexBuffer)]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.VertexBuffer | BufferUsage.Dynamic)]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.Staging)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.VertexBuffer)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.VertexBuffer | BufferUsage.Dynamic)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.Staging)]
    public void DeviceLevelUpdateWithoutPermissionThrowsAndNamesTheRemedy(GraphicsBackend backend, BufferUsage usage)
    {
        using var device = CreateDevice(backend);
        using var buffer = device.ResourceFactory.CreateBuffer(new BufferDescription(64, usage));
        buffer.Name = "permission-test";
        Assert.False(buffer.AllowUpdateBufferOutsideCommandList);

        var payload = new uint[16];
        var ex = Assert.Throws<VeldridException>(() => device.UpdateBufferOutsideCommandList(buffer, 0, payload));
        Assert.Contains(nameof(BufferDescription.AllowUpdateBufferOutsideCommandList), ex.Message);
        Assert.Contains("permission-test", ex.Message);
        Assert.Contains(nameof(CommandList.UpdateBuffer), ex.Message);

        // Every public overload funnels through the same root check.
        Assert.Throws<VeldridException>(() => device.UpdateBufferOutsideCommandList(buffer, 0, 7u));
        Assert.Throws<VeldridException>(() => device.UpdateBufferOutsideCommandList(buffer, 0, (ReadOnlySpan<uint>)payload));
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.VertexBuffer | BufferUsage.Dynamic)]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.Staging)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.VertexBuffer | BufferUsage.Dynamic)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.Staging)]
    public void DeviceLevelUpdateWithPermissionLandsBytes(GraphicsBackend backend, BufferUsage usage)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        using var source = factory.CreateBuffer(new BufferDescription(64, usage, structureByteStride: 0, rawBuffer: false, allowUpdateBufferOutsideCommandList: true));
        Assert.True(source.AllowUpdateBufferOutsideCommandList);

        var payload = new uint[16];
        for (int i = 0; i < payload.Length; i++) payload[i] = 0xA0000000u | (uint)i;
        device.UpdateBufferOutsideCommandList(source, 0, payload);

        // Readback through a Staging copy destination that itself never needs the permission.
        using var readback = factory.CreateBuffer(new BufferDescription(64, BufferUsage.Staging));
        Assert.False(readback.AllowUpdateBufferOutsideCommandList);
        using var commands = factory.CreateCommandList();
        commands.Begin(); commands.CopyBuffer(source, 0, readback, 0, 64); commands.End();
        device.SubmitCommands(commands); device.WaitForIdle();
        var view = device.Map<uint>(readback, MapMode.Read);
        try { for (int i = 0; i < payload.Length; i++) Assert.Equal(payload[i], view[i]); }
        finally { device.Unmap(readback); }
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void CommandListUpdateNeverNeedsThePermission(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        using var gpuPrivate = factory.CreateBuffer(new BufferDescription(64, BufferUsage.VertexBuffer));
        using var dynamic = factory.CreateBuffer(new BufferDescription(64, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        using var readback = factory.CreateBuffer(new BufferDescription(64, BufferUsage.Staging));
        Assert.False(gpuPrivate.AllowUpdateBufferOutsideCommandList);
        Assert.False(dynamic.AllowUpdateBufferOutsideCommandList);

        var first = new uint[16]; var second = new uint[16];
        for (int i = 0; i < 16; i++) { first[i] = 0x11110000u | (uint)i; second[i] = 0x22220000u | (uint)i; }

        using var commands = factory.CreateCommandList();
        commands.Begin();
        commands.UpdateBuffer(gpuPrivate, 0, first);
        commands.UpdateBuffer(dynamic, 0, second);
        commands.CopyBuffer(gpuPrivate, 0, readback, 0, 32);
        commands.CopyBuffer(dynamic, 32, readback, 32, 32);
        commands.End();
        device.SubmitCommands(commands); device.WaitForIdle();
        var view = device.Map<uint>(readback, MapMode.Read);
        try
        {
            for (int i = 0; i < 8; i++) Assert.Equal(first[i], view[i]);
            for (int i = 8; i < 16; i++) Assert.Equal(second[i], view[i]);
        }
        finally { device.Unmap(readback); }
    }

    [Fact]
    public void DescriptionCarriesTheFlagInEqualityAndHash()
    {
        var without = new BufferDescription(64, BufferUsage.VertexBuffer);
        var withFlag = new BufferDescription(64, BufferUsage.VertexBuffer, 0, false, allowUpdateBufferOutsideCommandList: true);
        Assert.False(without.AllowUpdateBufferOutsideCommandList);
        Assert.True(withFlag.AllowUpdateBufferOutsideCommandList);
        Assert.NotEqual(without, withFlag);
        Assert.NotEqual(without.GetHashCode(), withFlag.GetHashCode());
        Assert.Equal(withFlag, new BufferDescription(64, BufferUsage.VertexBuffer, 0, false, true));
        Assert.False(new BufferDescription(64, BufferUsage.VertexBuffer, 0).AllowUpdateBufferOutsideCommandList);
        Assert.False(new BufferDescription(64, BufferUsage.VertexBuffer, 0, false).AllowUpdateBufferOutsideCommandList);
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void TypedShapeStorageIsSequencedByDesignAndNeverGrantsThePermission(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var bundle = LoadBundle(device.ResourceFactory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, device.ResourceFactory);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer | BufferUsage.Dynamic);
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer | BufferUsage.Dynamic);
            // Typed storage writes only through CommandList.UpdateBuffer; even Dynamic typed storage does not opt in.
            Assert.False(shape.Projection.PrivateBuffer.AllowUpdateBufferOutsideCommandList);
            Assert.False(vertices.Buffer.AllowUpdateBufferOutsideCommandList);
            Assert.Throws<VeldridException>(() => device.UpdateBufferOutsideCommandList(shape.Projection.PrivateBuffer, 0, new ImGuiProjection()));
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }

    private static GraphicsDevice CreateDevice(GraphicsBackend backend) => backend == GraphicsBackend.Direct3D11
        ? GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions())
        : GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions());

    private static PrecompiledShaderResult LoadBundle(ResourceFactory factory)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContractTests.ImGui.vdshader")!;
        using var reader = new StreamReader(source);
        return factory.CreateFromBundle(reader.ReadToEnd());
    }
}