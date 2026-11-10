using System;
using System.Numerics;
using Veldrid;
using Xunit;
using static Veldrid.ContractTests.ShapeTestHarness;

namespace Veldrid.ContractTests;

public sealed class ExplicitUniformStorageTests
{
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void StorageMustBeExplicitAndUsageIsPreserved(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var bundle = LoadBundle(device.ResourceFactory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, device.ResourceFactory);
            using var commands = device.ResourceFactory.CreateCommandList();
            commands.Begin();
            // No implicit allocation at Bind: writing or binding before CreateBuffer is an error naming the remedy.
            Assert.Throws<InvalidOperationException>(() => commands.UpdateBuffer(shape.Projection, new ImGuiProjection()));
            Assert.Throws<InvalidOperationException>(() => shape.Projection.With());
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(0));
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(BufferUsage.Staging));
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(BufferUsage.UniformBuffer | BufferUsage.VertexBuffer));
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            Assert.Equal(BufferUsage.UniformBuffer, shape.Projection.PrivateBuffer.Usage);
            Assert.Throws<InvalidOperationException>(() => shape.Projection.CreateBuffer(BufferUsage.UniformBuffer));
            using var other = ShaderPipelineShape.Bind<ImGuiShape>(bundle, device.ResourceFactory);
            other.Projection.CreateBuffer(BufferUsage.UniformBuffer | BufferUsage.Dynamic);
            Assert.Equal(BufferUsage.UniformBuffer | BufferUsage.Dynamic, other.Projection.PrivateBuffer.Usage);
            using var vertex = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            Assert.Equal(BufferUsage.VertexBuffer, vertex.Usage);
            Assert.Throws<ArgumentException>(() => shape.Vertices.CreateBuffer(3, BufferUsage.UniformBuffer));
            shape.Projection.Dispose();
            Assert.Throws<ObjectDisposedException>(() => commands.UpdateBuffer(shape.Projection, new ImGuiProjection()));
            commands.End();
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }

    [Fact]
    public void D3D11PartialUniformUpdateIsRejectedWithoutWidening()
    {
        using var device = CreateDevice(GraphicsBackend.Direct3D11);
        var bundle = LoadBundle(device.ResourceFactory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, device.ResourceFactory);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            using var commands = device.ResourceFactory.CreateCommandList();
            commands.Begin();
            var projection = Matrix4x4.Identity;
            Assert.Throws<NotSupportedException>(() => commands.UpdateBuffer(shape.Projection, p => p.projection, in projection));
            commands.UpdateBuffer(shape.Projection, new ImGuiProjection { projection = projection });
            commands.End();
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11, false)]
    [InlineData(GraphicsBackend.Vulkan, false)]
    [InlineData(GraphicsBackend.Vulkan, true)]
    public void NonDynamicUpdatesBetweenDrawsRetainCommandOrder(GraphicsBackend backend, bool memberUpdates)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = CreateWhiteTexture(device);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            using var gpu = ImGuiFamilyResources.Create(factory, bundle.Shaders, shape.Vertices, shape.Projection, shape.Projection.With(),
                shape.FontSampler, device.PointSampler, shape.FontTexture, white, target.OutputDescription);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            commands.UpdateBuffer(vertices, 0, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            });
            var value = new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 };
            commands.UpdateBuffer(shape.Projection, value);
            gpu.Bind(commands); commands.SetVertexBuffer(shape.Vertices, vertices);
            commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);
            value.UseLegacyColorSpaceHandling = 0;
            if (memberUpdates) commands.UpdateBuffer(shape.Projection, p => p.UseLegacyColorSpaceHandling, in value.UseLegacyColorSpaceHandling);
            else commands.UpdateBuffer(shape.Projection, value);
            commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);
            value.UseLegacyColorSpaceHandling = 1;
            if (memberUpdates) commands.UpdateBuffer(shape.Projection, p => p.UseLegacyColorSpaceHandling, in value.UseLegacyColorSpaceHandling);
            else commands.UpdateBuffer(shape.Projection, value);
            commands.SetScissorRect(0, 16, 0, 8, 16); commands.Draw(3);
            commands.CopyTexture(color, staging); commands.End(); device.SubmitCommands(commands); device.GpuOrderingBarrier();
            var pixels = device.Map<RgbaByte>(staging, MapMode.Read);
            try
            {
                Assert.InRange((int)pixels[4, 8].R, 127, 129);
                Assert.InRange((int)pixels[12, 8].R, 53, 57);
                Assert.InRange((int)pixels[20, 8].R, 127, 129);
            }
            finally { device.Unmap(staging); }
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}