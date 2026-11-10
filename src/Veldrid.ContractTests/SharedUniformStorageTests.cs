using System;
using System.Numerics;
using Veldrid;
using Xunit;
using static Veldrid.ContractTests.ShapeTestHarness;

namespace Veldrid.ContractTests;

/// <summary>
///     ShaderUniformBuffer&lt;T&gt;: ONE typed uniform buffer bound (through each block's With) by sets of several shapes, written by ONE
///     command-ordered UpdateBuffer. Lets a consumer whose GPU behaviour is "one shared view buffer, many pipelines" (Silky) keep
///     that behaviour byte-for-byte while every binding block is still name/type-verified at Bind.
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
            Assert.Equal(usage, shared.Buffer.Usage);
            Assert.False(shared.Buffer.AllowUpdateBufferOutsideCommandList);

            using var color = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
            using var staging = factory.CreateTexture(TextureDescription.Texture2D(24, 16, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
            using var white = CreateWhiteTexture(device);
            using var target = factory.CreateFramebuffer(new FramebufferDescription(null, color));
            // ONE vertex buffer for both families (same verified vertex type) - as Silky binds one NvgVertex buffer across 25 families.
            using var vertices = first.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer);
            using var firstGpu = ImGuiFamilyResources.Create(factory, bundle.Shaders, first.Vertices, first.Projection, first.Projection.With(shared),
                first.FontSampler, device.PointSampler, first.FontTexture, white, target.OutputDescription);
            using var secondGpu = ImGuiFamilyResources.Create(factory, bundle.Shaders, second.Vertices, second.Projection, second.Projection.With(shared),
                second.FontSampler, device.PointSampler, second.FontTexture, white, target.OutputDescription);

            var triangle = new ImGuiVertex[]
            {
                new() { vsin_position = new Vector2(-1, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(3, -1), vsin_color = 0xff808080 },
                new() { vsin_position = new Vector2(-1, 3), vsin_color = 0xff808080 },
            };
            using var commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetFramebuffer(target); commands.SetFullViewports(); commands.ClearColorTarget(0, RgbaFloat.Black);
            commands.UpdateBuffer(vertices, 0, triangle);

            var value = new ImGuiProjection { projection = Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 };
            commands.UpdateBuffer(shared, value);                                 // ONE upload ...
            firstGpu.Bind(commands); commands.SetVertexBuffer(first.Vertices, vertices);
            commands.SetScissorRect(0, 0, 0, 8, 16); commands.Draw(3);            // ... seen by the first family
            value.UseLegacyColorSpaceHandling = 0;
            commands.UpdateBuffer(shared, value);                                 // ONE upload ...
            secondGpu.Bind(commands);                                             // vertex buffer stays bound: same TVertex, no rebind
            commands.SetScissorRect(0, 8, 0, 8, 16); commands.Draw(3);            // ... seen by the second family, in command order
            value.UseLegacyColorSpaceHandling = 1;
            commands.UpdateBuffer(shared, value);
            firstGpu.Bind(commands);
            commands.SetScissorRect(0, 16, 0, 8, 16); commands.Draw(3);           // ... and the first family again sees the newest value
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

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void SharedStorageIsWrittenOnlyThroughItsOwnHandle(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            using var first = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var second = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            using var shared = first.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            var projection = Matrix4x4.Identity;
            // Blocks without a private buffer cannot be updated per slot: one storage, one UpdateBuffer.
            Assert.Throws<InvalidOperationException>(() => commands.UpdateBuffer(first.Projection, new ImGuiProjection()));
            Assert.Throws<InvalidOperationException>(() => commands.UpdateBuffer(second.Projection, p => p.projection, in projection));
            Assert.Throws<InvalidOperationException>(() => first.Projection.With());
            // Allocation is explicit and validated the same way as private storage.
            Assert.Throws<ArgumentException>(() => second.Projection.CreateSharedBuffer(BufferUsage.Staging));
            Assert.Throws<ArgumentException>(() => second.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer | BufferUsage.VertexBuffer));
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
            Assert.Throws<InvalidOperationException>(() => onB.Projection.With(shared));
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
    public void DisposedSharedStorageCannotBeWrittenOrBound(GraphicsBackend backend)
    {
        using var device = CreateDevice(backend);
        var factory = device.ResourceFactory;
        var bundle = LoadBundle(factory);
        try
        {
            var first = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
            using var second = ShaderPipelineShape.Bind<SecondFamilyShape>(bundle, factory);
            var shared = first.Projection.CreateSharedBuffer(BufferUsage.UniformBuffer);
            using var commands = factory.CreateCommandList();
            commands.Begin();
            // Disposing the creating SHAPE does not dispose storage another shape still uses.
            first.Dispose();
            Assert.False(shared.Buffer.IsDisposed);
            commands.UpdateBuffer(shared, new ImGuiProjection());
            // Disposing the storage itself is what invalidates it: writes and new bindings are refused.
            shared.Dispose();
            Assert.Throws<ObjectDisposedException>(() => commands.UpdateBuffer(shared, new ImGuiProjection()));
            Assert.Throws<ObjectDisposedException>(() => second.Projection.With(shared));
            commands.End();
        }
        finally { foreach (var shader in bundle.Shaders) shader.Dispose(); }
    }
}