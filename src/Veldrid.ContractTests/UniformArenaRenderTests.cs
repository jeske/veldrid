using System;
using System.Numerics;
using Veldrid;
using Xunit;
using static Veldrid.ContractTests.ShapeTestHarness;

namespace Veldrid.ContractTests;

public sealed class UniformArenaRenderTests
{
    [ShaderBundle("ImGui")]
    public sealed partial class ArenaShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        [UniformBufferBindingTimeOffsetSelectable] public UniformBlock<ImGuiProjection> Projection;
        public TextureSlot FontTexture;
        public SamplerSlot FontSampler;
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void TwoElementsOneUploadSelectDifferentUniformValues(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ArenaShape>(bundle, factory);
            using var arena = shape.Projection.CreateArena(device, 2, BufferUsage.UniformBuffer);
            Assert.Equal(0u, arena.StrideBytes % device.UniformBufferMinOffsetAlignment);
            // An offset-selectable block binds an arena, never whole-block storage; and vice versa.
            Assert.Throws<InvalidOperationException>(() => shape.Projection.CreateBuffer(BufferUsage.UniformBuffer));
            arena.Stage(new UniformArenaElement(0), new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 });
            arena.Stage(new UniformArenaElement(1), new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 0 });
            using var color = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = CreateWhiteTexture(device);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer | BufferUsage.Dynamic);
            using var gpu = ImGuiFamilyResources.Create(factory, bundle.Shaders, shape.Vertices, shape.Projection, shape.Projection.With(arena),
                shape.FontSampler, device.PointSampler, shape.FontTexture, white, target.OutputDescription);
            using var commands = factory.CreateCommandList();
            commands.Begin(); commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            commands.UpdateBuffer(vertices, 0, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            });
            commands.UpdateBuffer(arena);                                        // ONE upload for both elements
            commands.SetPipeline(gpu.Pipeline);
            commands.SetVertexBuffer(shape.Vertices, vertices);
            commands.SetGraphicsResourceSet(gpu.TextureSet);
            // The element replaces the raw dynamic byte offset; the SAME set is rebound with a different element.
            commands.SetGraphicsResourceSet(gpu.MainSet, new UniformArenaElement(0)); commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);
            commands.SetGraphicsResourceSet(gpu.MainSet, new UniformArenaElement(1)); commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);
            // Element count must match the set's offset-selectable bindings; out-of-range elements are refused by the arena.
            Assert.Throws<ShaderContractException>(() => commands.SetGraphicsResourceSet(gpu.MainSet));
            Assert.Throws<ArgumentOutOfRangeException>(() => commands.SetGraphicsResourceSet(gpu.MainSet, new UniformArenaElement(2)));
            commands.CopyTexture(color, staging); commands.End(); device.SubmitCommands(commands); device.WaitForIdle();
            var pixels = device.Map<RgbaByte>(staging, MapMode.Read);
            try { Assert.InRange((int)pixels[4, 8].R, 127, 129); Assert.InRange((int)pixels[12, 8].R, 53, 57); }
            finally { device.Unmap(staging); }
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}