using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class UniformArenaRenderTests
{
    [ShaderBundle("ImGui")]
    public sealed partial class ArenaShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        [DynamicUniform] public UniformBlock<ImGuiProjection> Projection;
        public TextureSlot FontTexture;
        public SamplerSlot FontSampler;
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void TwoElementsOneUploadSelectDifferentUniformValues(GraphicsBackend backend)
    {
        using var device = backend == GraphicsBackend.Direct3D11 ? GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions()) : GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions());
        var factory = device.ResourceFactory;
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContractTests.ImGui.vdshader")!;
        using var reader = new StreamReader(source);
        var bundle = factory.CreateFromBundle(reader.ReadToEnd());
        try
        {
            using var shape = ShaderPipelineShape.Bind<ArenaShape>(bundle, factory);
            using var arena = shape.Projection.CreateArena(device, 2);
            Assert.Equal(0u, arena.StrideBytes % device.UniformBufferMinOffsetAlignment);
            Assert.Throws<ArgumentOutOfRangeException>(() => shape.Projection.Select(arena, new UniformArenaElement(2)));
            arena.Stage(new UniformArenaElement(0), new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 });
            arena.Stage(new UniformArenaElement(1), new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 0 });
            using var color = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            device.UpdateTexture(white, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = shape.Vertices.CreateBuffer(3);
            var raster = RasterizerStateDescription.CULL_NONE; raster.ScissorTestEnabled = true;
            using var pipeline = shape.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend, new DepthStencilStateDescription(false, false, ComparisonKind.Always), raster, PrimitiveTopology.TriangleList, target.OutputDescription);
            shape.FontTexture.Set(white); shape.FontSampler.Set(device.PointSampler);
            using var commands = factory.CreateCommandList();
            commands.Begin(); commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            vertices.Update(commands, new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            });
            arena.Upload(commands);
            commands.SetPipeline(pipeline); shape.Vertices.Bind(commands, vertices);
            shape.Projection.Select(arena, new UniformArenaElement(0));
            shape.BindResourceSets(commands); commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);
            shape.Projection.Select(arena, new UniformArenaElement(1));
            shape.BindResourceSets(commands); commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);
            Assert.Equal(2, shape.CachedResourceSetCount); // offset selection does not create another resource set
            commands.CopyTexture(color, staging); commands.End(); device.SubmitCommands(commands); device.WaitForIdle();
            var pixels = device.Map<RgbaByte>(staging, MapMode.Read);
            try { Assert.InRange((int)pixels[4, 8].R, 127, 129); Assert.InRange((int)pixels[12, 8].R, 53, 57); }
            finally { device.Unmap(staging); }
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}