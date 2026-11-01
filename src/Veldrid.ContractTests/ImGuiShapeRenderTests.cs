using System.Numerics;
using Veldrid;
using Xunit;
using static Veldrid.ContractTests.ShapeTestHarness;

namespace Veldrid.ContractTests;

/// <summary>Finite offscreen rendering/readback through the actual ImGui shader bundle and typed shape, using the same-name API.</summary>
public sealed class ImGuiShapeRenderTests
{
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void TypedShapeRendersKnownColor(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = CreateWhiteTexture(device);
            using var framebuffer = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer | BufferUsage.Dynamic);
            using var gpu = ImGuiFamilyResources.Create(factory, bundle.Shaders, shape.Vertices, shape.Projection, shape.Projection.With(),
                shape.FontSampler, device.PointSampler, shape.FontTexture, white, framebuffer.OutputDescription, scissorTest: false);

            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.SetFullViewports();
            commands.SetFullScissorRects();
            commands.ClearColorTarget(0, RgbaFloat.Black);
            commands.UpdateBuffer(shape.Projection, new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 });
            commands.UpdateBuffer(vertices, 0, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff0000ff },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff0000ff },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff0000ff },
            });
            gpu.Bind(commands);
            commands.SetVertexBuffer(shape.Vertices, vertices);
            commands.Draw(3);

            // A set from ANOTHER shape instance with an IDENTICAL layout is legal (GPU descriptor-set compatibility); a set whose
            // layout differs (the arena shape's offset-selectable Projection at set 0) is refused.
            using var other = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var otherLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(other.FontTexture));
            using var otherSet = factory.CreateResourceSet(new ResourceSetDescription(otherLayout, other.FontTexture.With(white)));
            commands.SetGraphicsResourceSet(otherSet);
            using var arenaShape = ShaderPipelineShape.Bind<UniformArenaRenderTests.ArenaShape>(bundle, factory);
            using var arena = arenaShape.Projection.CreateArena(device, 1, BufferUsage.UniformBuffer);
            using var arenaLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(arenaShape.Projection, arenaShape.FontSampler));
            using var arenaSet = factory.CreateResourceSet(new ResourceSetDescription(arenaLayout, arenaShape.Projection.With(arena), arenaShape.FontSampler.With(device.PointSampler)));
            Assert.Throws<ShaderContractException>(() => commands.SetGraphicsResourceSet(arenaSet, new UniformArenaElement(0)));
            // Binding a disposed texture through a slot is refused at binding time.
            using var removed = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            removed.Dispose();
            Assert.Throws<System.ObjectDisposedException>(() => shape.FontTexture.With(removed));

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