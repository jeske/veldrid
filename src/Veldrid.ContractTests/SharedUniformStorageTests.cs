using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     ShaderUniformBuffer&lt;T&gt;: ONE typed uniform buffer bound by UniformBlock slots on several shapes, written by ONE
///     command-ordered Update. Exists so a consumer whose GPU behaviour is "one shared view buffer, many pipelines" (Silky)
///     can keep that behaviour byte-for-byte while every sharing slot is still name/type-verified at Bind.
/// </summary>
public sealed class SharedUniformStorageTests
{
    /// <summary>A second shape family over the same bundle: same verified Projection block, different shape instance/pipeline.</summary>
    [ShaderBundle("ImGui")]
    public sealed partial class SecondFamilyShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        public UniformBlock<ImGuiProjection> Projection;
        public SamplerSlot FontSampler;
        public TextureSlot FontTexture;
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11, BufferUsage.UniformBuffer)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.UniformBuffer)]
    [InlineData(GraphicsBackend.Vulkan, BufferUsage.UniformBuffer | BufferUsage.Dynamic)]
    public void OneUpdateIsObservedByEverySharingShapeInCommandOrder(GraphicsBackend backend, BufferUsage usage)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var first = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var second = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            using var shared = first.Projection.CreateSharedBuffer(usage);
            second.Projection.Share(shared);
            Assert.Equal(usage, shared.Buffer.Usage);
            Assert.False(shared.Buffer.AllowUpdateBufferOutsideCommandList);

            using var color = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            device.UpdateTexture(white, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var firstVertices = first.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            using var secondVertices = second.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            var raster = RasterizerStateDescription.CULL_NONE; raster.ScissorTestEnabled = true;
            var depth = new DepthStencilStateDescription(false, false, ComparisonKind.Always);
            using var firstPipeline = first.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList, target.OutputDescription);
            using var secondPipeline = second.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend, depth, raster, PrimitiveTopology.TriangleList, target.OutputDescription);
            first.FontTexture.Set(white); first.FontSampler.Set(device.PointSampler);
            second.FontTexture.Set(white); second.FontSampler.Set(device.PointSampler);

            var triangle = new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            };
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            firstVertices.Update(commands, triangle);
            secondVertices.Update(commands, triangle);

            var value = new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 };
            shared.Update(commands, value);                                   // ONE upload ...
            commands.SetPipeline(firstPipeline); first.Vertices.Bind(commands, firstVertices); first.BindResourceSets(commands);
            commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);        // ... seen by the first family
            value.UseLegacyColorSpaceHandling = 0;
            shared.Update(commands, value);                                   // ONE upload ...
            commands.SetPipeline(secondPipeline); second.Vertices.Bind(commands, secondVertices); second.BindResourceSets(commands);
            commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);        // ... seen by the second family, in command order
            value.UseLegacyColorSpaceHandling = 1;
            shared.Update(commands, value);
            commands.SetPipeline(firstPipeline); first.Vertices.Bind(commands, firstVertices); first.BindResourceSets(commands);
            commands.SetScissorRect(0, 16, 0, 8, 16); commands.Draw(3);       // ... and the first family again sees the newest value
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

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void SharingSlotsAreWrittenOnlyThroughTheStorage(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var first = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var second = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            using var shared = first.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer);
            second.Projection.Share(shared);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            var projection = Matrix4x4.Identity;
            // Per-slot writes are refused on both the creator and the sharer: one storage, one Update.
            Assert.Throws<InvalidOperationException>(() => first.Projection.Update(commands, new ImGuiProjection()));
            Assert.Throws<InvalidOperationException>(() => second.Projection.Update(commands, new ImGuiProjection()));
            Assert.Throws<InvalidOperationException>(() => second.Projection.UpdateMember(commands, p => p.projection, in projection));
            // A slot binds exactly one kind of storage.
            Assert.Throws<InvalidOperationException>(() => first.Projection.CreateBuffer(BufferUsage.UniformBuffer));
            Assert.Throws<InvalidOperationException>(() => first.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer));
            Assert.Throws<InvalidOperationException>(() => second.Projection.Share(shared));
            using var owned = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            owned.Projection.CreateBuffer(BufferUsage.UniformBuffer);
            Assert.Throws<InvalidOperationException>(() => owned.Projection.Share(shared));
            // Allocation is explicit and validated the same way as owned storage.
            using var other = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            Assert.Throws<ArgumentException>(() => other.Projection.CreateSharedBuffer(BufferUsage.Staging));
            Assert.Throws<ArgumentException>(() => other.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer | BufferUsage.VertexBuffer));
            commands.End();
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }

    [Fact]
    public void SharingAcrossDevicesIsRejected()
    {
        using var deviceA = CreateDevice(GraphicsBackend.Direct3D11);
        using var deviceB = CreateDevice(GraphicsBackend.Direct3D11);
        var bundleA = LoadBundle(deviceA.ResourceFactory);
        var bundleB = LoadBundle(deviceB.ResourceFactory);
        try
        {
            using var onA = ShaderPipelineShape.Bind<ImGuiShape>(bundleA, deviceA.ResourceFactory);
            using var onB = ShaderPipelineShape.Bind<SecondFamilyShape>(bundleB, deviceB.ResourceFactory);
            using var shared = onA.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer);
            Assert.Throws<InvalidOperationException>(() => onB.Projection.Share(shared));
        }
        finally
        {
            foreach (var shader in bundleA.Shaders) shader.Dispose();
            foreach (var shader in bundleB.Shaders) shader.Dispose();
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void DisposingStorageInvalidatesEverySharerButBlocksNeverDisposeIt(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            var first = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var second = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            var shared = first.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer);
            second.Projection.Share(shared);
            using var color = factory.CreateTexture(TextureDescription.Texture2D(4, 4, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var white = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            using var secondVertices = second.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            using var secondPipeline = second.CreateGraphicsPipeline(BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always), RasterizerStateDescription.CULL_NONE, PrimitiveTopology.TriangleList, target.OutputDescription);
            second.FontTexture.Set(white); second.FontSampler.Set(device.PointSampler);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target);
            // Disposing the creating SHAPE does not dispose storage another shape still binds.
            first.Dispose();
            Assert.False(shared.Buffer.IsDisposed);
            shared.Update(commands, new ImGuiProjection());
            commands.SetPipeline(secondPipeline); second.Vertices.Bind(commands, secondVertices);
            second.BindResourceSets(commands);                                  // live storage: binds fine
            // Disposing the storage itself is what invalidates the sharers.
            shared.Dispose();
            Assert.Throws<ObjectDisposedException>(() => shared.Update(commands, new ImGuiProjection()));
            // BindResourceSets re-checks every bound slot's liveness on EVERY call (before the set cache), so the sharer fails fast.
            Assert.Throws<ObjectDisposedException>(() => second.BindResourceSets(commands));
            commands.End();
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