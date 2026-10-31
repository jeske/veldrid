using System;
using System.IO;
using System.Reflection;
using Veldrid;

namespace Veldrid.ContractTests;

/// <summary>
///     Shared GPU plumbing for the ImGui-bundle test shapes (ImGuiShape / SecondFamilyShape / ArenaShape all declare the same four
///     GLSL names). Everything here is the SAME-NAME API: layouts from slots, sets from slot bindings, pipeline from a description.
/// </summary>
internal static class ShapeTestHarness
{
    internal static GraphicsDevice CreateDevice(GraphicsBackend backend) => backend == GraphicsBackend.Direct3D11
        ? GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions())
        : GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions());

    internal static PrecompiledShaderResult LoadBundle(ResourceFactory factory)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContractTests.ImGui.vdshader")!;
        using var reader = new StreamReader(source);
        return factory.CreateFromBundle(reader.ReadToEnd());
    }

    internal static Texture CreateWhiteTexture(GraphicsDevice device)
    {
        var white = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
        device.UpdateTexture(white, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
        return white;
    }

    /// <summary>Set 0 = {Projection, FontSampler}, set 1 = {FontTexture}: the ImGui bundle's two descriptor sets, plus the pipeline.</summary>
    internal sealed class ImGuiFamilyResources : IDisposable
    {
        public ResourceLayout MainLayout = null!;
        public ResourceLayout TextureLayout = null!;
        public ResourceSet MainSet = null!;
        public ResourceSet TextureSet = null!;
        public Pipeline Pipeline = null!;

        public static ImGuiFamilyResources Create(
            ResourceFactory factory,
            Shader[] shaders,
            VertexInput vertices,
            ShapeSlot projectionSlot,
            ShapeResourceBinding projectionBinding,
            SamplerSlot samplerSlot,
            Sampler sampler,
            TextureSlot textureSlot,
            Texture texture,
            OutputDescription outputs,
            bool scissorTest = true)
        {
            var r = new ImGuiFamilyResources();
            r.MainLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(projectionSlot, samplerSlot));
            r.TextureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(textureSlot));
            r.MainSet = factory.CreateResourceSet(new ResourceSetDescription(r.MainLayout, projectionBinding, samplerSlot.With(sampler)));
            r.TextureSet = factory.CreateResourceSet(new ResourceSetDescription(r.TextureLayout, textureSlot.With(texture)));
            var raster = RasterizerStateDescription.CULL_NONE; raster.ScissorTestEnabled = scissorTest;
            r.Pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                raster,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertices }, shaders),
                new[] { r.MainLayout, r.TextureLayout },
                outputs));
            return r;
        }

        public void Bind(CommandList commands, params UniformArenaElement[] mainSetElements)
        {
            commands.SetPipeline(Pipeline);
            commands.SetGraphicsResourceSet(MainSet, mainSetElements);
            commands.SetGraphicsResourceSet(TextureSet);
        }

        public void Dispose()
        {
            Pipeline.Dispose();
            TextureSet.Dispose();
            MainSet.Dispose();
            TextureLayout.Dispose();
            MainLayout.Dispose();
        }
    }
}