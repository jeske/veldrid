using System.IO;
using System.Numerics;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>Finite offscreen rendering/readback through the actual ImGui shader bundle and typed shape.</summary>
public sealed class ImGuiShapeRenderTests
{
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void TypedShapeRendersKnownColor(GraphicsBackend backend)
    {
        using var device = backend == GraphicsBackend.Direct3D11
            ? GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions())
            : GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions());
        var factory = device.ResourceFactory;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContractTests.ImGui.vdshader")!;
        using var reader = new StreamReader(stream);
        var bundle = factory.CreateFromBundle(reader.ReadToEnd());
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            device.UpdateTexture(white, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
            using var framebuffer = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3);
            using var pipeline = shape.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always), RasterizerStateDescription.CULL_NONE,
                PrimitiveTopology.TriangleList, framebuffer.OutputDescription);
            shape.FontTexture.Set(white);
            shape.FontSampler.Set(device.PointSampler);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.SetFullViewports();
            commands.SetFullScissorRects();
            commands.ClearColorTarget(0, RgbaFloat.Black);
            shape.Projection.Update(commands, new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 });
            vertices.Update(commands, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff0000ff },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff0000ff },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff0000ff },
            });
            commands.SetPipeline(pipeline);
            shape.Vertices.Bind(commands, vertices);
            shape.BindResourceSets(commands);
            commands.Draw(3);
            Assert.Equal(2, shape.CachedResourceSetCount);
            // Eviction after recording must retain already-recorded GPU references, but remove cache ownership immediately.
            shape.FontTexture.Forget(white);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));
            Assert.Equal(1, shape.CachedResourceSetCount);
            Assert.Throws<ShaderContractException>(() => shape.BindResourceSets(commands));
            shape.FontTexture.Set(white);
            shape.BindResourceSets(commands);
            Assert.Equal(2, shape.CachedResourceSetCount);
            commands.Draw(3);
            using var removed = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            removed.Dispose();
            Assert.Throws<System.ObjectDisposedException>(() => shape.FontTexture.Set(removed));
            commands.CopyTexture(color, staging);
            commands.End();
            device.SubmitCommands(commands);
            device.WaitForIdle();
            var mapped = device.Map<RgbaByte>(staging, MapMode.Read);
            try
            {
                var pixel = mapped[8, 8];
                Assert.Equal(255, pixel.R);
                Assert.Equal(0, pixel.G);
                Assert.Equal(0, pixel.B);
                Assert.Equal(255, pixel.A);
            }
            finally { device.Unmap(staging); }
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}