using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using Veldrid;
using Xunit;

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
            Assert.Null(shape.Projection.Buffer);
            using var commands = device.ResourceFactory.CreateCommandList();
            commands.Begin();
            Assert.Throws<InvalidOperationException>(() => shape.Projection.Update(commands, new ImGuiProjection()));
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(0));
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(BufferUsage.Staging));
            Assert.Throws<ArgumentException>(() => shape.Projection.CreateBuffer(BufferUsage.UniformBuffer | BufferUsage.VertexBuffer));
            Assert.Null(shape.Projection.Buffer);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            Assert.Equal(BufferUsage.UniformBuffer, shape.Projection.Buffer.Usage);
            Assert.Throws<InvalidOperationException>(() => shape.Projection.CreateBuffer(BufferUsage.UniformBuffer));
            using var other = ShaderPipelineShape.Bind<ImGuiShape>(bundle, device.ResourceFactory);
            other.Projection.CreateBuffer(BufferUsage.UniformBuffer | BufferUsage.Dynamic);
            Assert.Equal(BufferUsage.UniformBuffer | BufferUsage.Dynamic, other.Projection.Buffer.Usage);
            using var vertex = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            Assert.Equal(BufferUsage.VertexBuffer, vertex.Buffer.Usage);
            Assert.Throws<ArgumentException>(() => shape.Vertices.CreateBuffer(3, BufferUsage.UniformBuffer));
            shape.Projection.Dispose();
            Assert.Throws<ObjectDisposedException>(() => shape.Projection.Update(commands, new ImGuiProjection()));
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
            Assert.Throws<NotSupportedException>(() => shape.Projection.UpdateMember(commands, p => p.projection, in projection));
            shape.Projection.Update(commands, new ImGuiProjection { projection = projection });
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
            using var white = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            device.UpdateTexture(white, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            var raster = RasterizerStateDescription.CULL_NONE; raster.ScissorTestEnabled = true;
            using var pipeline = shape.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always), raster, PrimitiveTopology.TriangleList, target.OutputDescription);
            shape.FontTexture.Set(white);
            shape.FontSampler.Set(device.PointSampler);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            vertices.Update(commands, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            });
            var value = new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 };
            shape.Projection.Update(commands, value);
            commands.SetPipeline(pipeline); shape.Vertices.Bind(commands, vertices); shape.BindResourceSets(commands);
            commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);
            value.UseLegacyColorSpaceHandling = 0;
            if (memberUpdates) shape.Projection.UpdateMember(commands, p => p.UseLegacyColorSpaceHandling, in value.UseLegacyColorSpaceHandling);
            else shape.Projection.Update(commands, value);
            commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);
            value.UseLegacyColorSpaceHandling = 1;
            if (memberUpdates) shape.Projection.UpdateMember(commands, p => p.UseLegacyColorSpaceHandling, in value.UseLegacyColorSpaceHandling);
            else shape.Projection.Update(commands, value);
            commands.SetScissorRect(0, 16, 0, 8, 16); commands.Draw(3);
            commands.CopyTexture(color, staging); commands.End(); device.SubmitCommands(commands); device.WaitForIdle();
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