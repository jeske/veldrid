using System.Numerics;
using Veldrid;
using Xunit;
using static Veldrid.ContractTests.ShapeTestHarness;

namespace Veldrid.ContractTests;

/// <summary>
///     The typed-slot-argument contract (BUGFIX_ShaderInterfaceContract_TypedSlotArguments.md): every slot/set/binding integer the
///     backend sees originates from a verified shape object, and the command-recording rules mirror the GPU's (no invented
///     invalidation). Same method names as the raw API throughout.
/// </summary>
public sealed class TypedBindingContractTests
{
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void LayoutsAndSetsRejectPositionalForeignIncompleteAndDuplicateSlots(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var other = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            using var white = CreateWhiteTexture(device);

            // A layout is one shape, one set, every slot of that set exactly once (order irrelevant).
            Assert.Throws<ShaderContractException>(() => new ResourceLayoutDescription(shape.Projection, other.FontSampler));   // foreign slot
            Assert.Throws<ShaderContractException>(() => new ResourceLayoutDescription(shape.Projection, shape.FontTexture));   // two sets
            Assert.Throws<ShaderContractException>(() => new ResourceLayoutDescription(shape.Projection));                      // incomplete
            Assert.Throws<ShaderContractException>(() => new ResourceLayoutDescription(shape.Projection, shape.Projection, shape.FontSampler)); // duplicate
            using var mainLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(shape.FontSampler, shape.Projection)); // order-free
            using var textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(shape.FontTexture));

            // A set binds exactly the layout's slots, each through its own With.
            Assert.Throws<ShaderContractException>(() => new ResourceSetDescription(mainLayout, shape.Projection.With()));                              // incomplete
            Assert.Throws<ShaderContractException>(() => new ResourceSetDescription(mainLayout, shape.Projection.With(), other.FontSampler.With(device.PointSampler))); // foreign
            Assert.Throws<ShaderContractException>(() => new ResourceSetDescription(mainLayout, shape.Projection.With(), shape.FontTexture.With(white)));          // wrong set
            Assert.Throws<ShaderContractException>(() => new ResourceSetDescription(mainLayout, shape.Projection.With(), shape.Projection.With(), shape.FontSampler.With(device.PointSampler))); // duplicate
            using var mainSet = factory.CreateResourceSet(new ResourceSetDescription(mainLayout, shape.FontSampler.With(device.PointSampler), shape.Projection.With()));
            using var textureSet = factory.CreateResourceSet(new ResourceSetDescription(textureLayout, shape.FontTexture.With(white)));

            // A pipeline takes ONE shape's vertex inputs and that shape's layouts in set order.
            using var otherTextureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(other.FontTexture));
            var raster = RasterizerStateDescription.CULL_NONE;
            var depth = new DepthStencilStateDescription(false, false, ComparisonKind.Always);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(4, 4, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            Assert.Throws<ShaderContractException>(() => factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(                        // shader set from another shape
                BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new VertexInput[] { other.Vertices }, bundle.Shaders), new[] { mainLayout, textureLayout }, target.OutputDescription)));
            Assert.Throws<ShaderContractException>(() => factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new VertexInput[] { shape.Vertices }, bundle.Shaders), new[] { mainLayout, otherTextureLayout }, target.OutputDescription)));
            Assert.Throws<ShaderContractException>(() => factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new VertexInput[] { shape.Vertices }, bundle.Shaders), new[] { textureLayout, mainLayout }, target.OutputDescription)));   // wrong set order
            Assert.Throws<ShaderContractException>(() => factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new VertexInput[] { shape.Vertices }, bundle.Shaders), new[] { mainLayout }, target.OutputDescription)));                 // missing a set
            using var pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new VertexInput[] { shape.Vertices }, bundle.Shaders), new[] { mainLayout, textureLayout }, target.OutputDescription));
            Assert.NotNull(pipeline);
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void CommandBindingsMirrorTheGpu(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var a = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var b = ShaderPipelineShape.Bind<SharedUniformStorageTests.SecondFamilyShape>(bundle, factory);
            a.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            b.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            using var white = CreateWhiteTexture(device);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(4, 4, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var vertices = a.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            using var gpuA = ImGuiFamilyResources.Create(factory, bundle.Shaders, a.Vertices, a.Projection, a.Projection.With(), a.FontSampler, device.PointSampler, a.FontTexture, white, target.OutputDescription, scissorTest: false);
            using var gpuB = ImGuiFamilyResources.Create(factory, bundle.Shaders, b.Vertices, b.Projection, b.Projection.With(), b.FontSampler, device.PointSampler, b.FontTexture, white, target.OutputDescription, scissorTest: false);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target); commands.SetFullViewports(); commands.SetFullScissorRects();
            commands.UpdateBuffer(vertices, 0, new ImGuiVertex[] { new(), new(), new() });
            commands.UpdateBuffer(a.Projection, new ImGuiProjection { projection = Matrix4x4.Identity });
            commands.UpdateBuffer(b.Projection, new ImGuiProjection { projection = Matrix4x4.Identity });

            // No pipeline: nothing can be bound through a shape.
            Assert.Throws<ShaderContractException>(() => commands.SetVertexBuffer(a.Vertices, vertices));
            Assert.Throws<ShaderContractException>(() => commands.SetGraphicsResourceSet(gpuA.MainSet));

            // Pipeline A: a draw needs A's vertex input and BOTH of A's sets bound.
            commands.SetPipeline(gpuA.Pipeline);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));                     // nothing bound
            commands.SetVertexBuffer(a.Vertices, vertices);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));                     // sets missing
            commands.SetGraphicsResourceSet(gpuA.MainSet);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));                     // set 1 missing
            commands.SetGraphicsResourceSet(gpuA.TextureSet);
            commands.Draw(3);
            // B's slots/sets are refused while A's pipeline is selected (provenance, not position).
            Assert.Throws<ShaderContractException>(() => commands.SetVertexBuffer(b.Vertices, vertices));
            Assert.Throws<ShaderContractException>(() => commands.SetGraphicsResourceSet(gpuB.MainSet));

            // Switch to pipeline B: the vertex buffer STAYS bound (same TVertex) exactly as on the GPU - no rebind required;
            // the sets do not carry over (they belong to A) and must be B's.
            commands.SetPipeline(gpuB.Pipeline);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));                     // A's sets are not B's
            commands.SetGraphicsResourceSet(gpuB.MainSet);
            commands.SetGraphicsResourceSet(gpuB.TextureSet);
            commands.Draw(3);                                                                  // vertex buffer survived the switch

            // Back to A: A's sets are still the ones bound at set 0/1? No - B's are. Rebind A's; vertex buffer still fine.
            commands.SetPipeline(gpuA.Pipeline);
            Assert.Throws<ShaderContractException>(() => commands.Draw(3));
            commands.SetGraphicsResourceSet(gpuA.MainSet);
            commands.SetGraphicsResourceSet(gpuA.TextureSet);
            commands.Draw(3);
            commands.End();
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}