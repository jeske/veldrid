using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.IO;

namespace Veldrid
{
    /// <summary>
    /// Can render draw lists produced by ImGui.
    /// Also exposes the ImGui input-event feed (<c>Add*Event</c>) and frame lifecycle
    /// (<see cref="BeginFrame(float)"/> / <see cref="EndFrame"/>) so any windowing host can drive it.
    /// Shaders: the embedded <c>ImGui.vdshader</c> bundle (Shaders/Source/ImGui.{vert,frag}.glsl compiled by Scripts/CompileShaders)
    /// bound through <see cref="ImGuiShape"/> - no per-backend bytecode, no hand-written layouts (D10).
    /// </summary>
    public partial class ImGuiRenderer : IDisposable
    {
        private const string BundleResourceName = "Veldrid.ImGui.Shaders.ImGui.vdshader";

        private GraphicsDevice _gd;
        private readonly Assembly _assembly;
        private ColorSpaceHandling _colorSpaceHandling;

        // Device objects
        private ShaderVertexBuffer<ImGuiVertex> _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private Texture _fontTexture;
        private PrecompiledShaderResult _bundle;
        private ImGuiShape _shape;
        private Pipeline _pipeline;
        private IntPtr _fontAtlasID = (IntPtr)1;

        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        // Image trackers: ImGui texture id -> view; the shape caches a resource set per distinct view.
        private readonly Dictionary<TextureView, IntPtr> _idsByView = new Dictionary<TextureView, IntPtr>();
        private readonly Dictionary<Texture, TextureView> _autoViewsByTexture = new Dictionary<Texture, TextureView>();
        private readonly Dictionary<IntPtr, TextureView> _viewsById = new Dictionary<IntPtr, TextureView>();
        private readonly List<IDisposable> _ownedResources = new List<IDisposable>();
        private int _lastAssignedID = 100;
        private bool _frameBegun;

        /// <summary>Constructs a new ImGuiRenderer.</summary>
        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height)
            : this(gd, outputDescription, width, height, ColorSpaceHandling.Legacy) { }

        /// <summary>Constructs a new ImGuiRenderer.</summary>
        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, ColorSpaceHandling colorSpaceHandling)
        {
            _gd = gd;
            _assembly = typeof(ImGuiRenderer).GetTypeInfo().Assembly;
            _colorSpaceHandling = colorSpaceHandling;
            _windowWidth = width;
            _windowHeight = height;

            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            ImGui.GetIO().Fonts.AddFontDefault();
            ImGui.GetIO().Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;

            CreateDeviceResources(gd, outputDescription);

            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription)
            => CreateDeviceResources(gd, outputDescription, _colorSpaceHandling);

        public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription, ColorSpaceHandling colorSpaceHandling)
        {
            _gd = gd;
            _colorSpaceHandling = colorSpaceHandling;
            ResourceFactory factory = gd.ResourceFactory;
            _indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            _indexBuffer.Name = "ImGui.NET Index Buffer";

            // The ONE shader path: embedded .vdshader -> CreateFromBundle -> shape (verified by name) -> pipeline from the shape.
            _bundle = factory.CreateFromBundle(GetEmbeddedResourceText(BundleResourceName));
            _shape = ShaderPipelineShape.Bind<ImGuiShape>(_bundle, factory);
            _vertexBuffer = _shape.Vertices.CreateBuffer(500);
            _shape.FontSampler.Set(gd.PointSampler);

            _pipeline = _shape.CreateGraphicsPipeline(
                BlendStateDescription.SINGLE_ALPHA_BLEND,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, true),
                PrimitiveTopology.TriangleList,
                outputDescription);
            _pipeline.Name = "ImGui.NET Pipeline";

            RecreateFontDeviceTexture(gd);
        }

        /// <summary>Gets or creates a handle for a texture to be drawn with ImGui. Pass the returned handle to Image() or ImageButton().</summary>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
        {
            if (!_idsByView.TryGetValue(textureView, out IntPtr id))
            {
                id = GetNextImGuiBindingID();
                _idsByView.Add(textureView, id);
                _viewsById.Add(id, textureView);
            }
            return id;
        }

        public void RemoveImGuiBinding(TextureView textureView)
        {
            if (_idsByView.TryGetValue(textureView, out IntPtr id))
            {
                _idsByView.Remove(textureView);
                _viewsById.Remove(id);
                _shape.FontTexture.Forget(textureView);
            }
        }

        private IntPtr GetNextImGuiBindingID()
        {
            int newID = _lastAssignedID++;
            return (IntPtr)newID;
        }

        /// <summary>Gets or creates a handle for a texture to be drawn with ImGui. Pass the returned handle to Image() or ImageButton().</summary>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
        {
            if (!_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                textureView = factory.CreateTextureView(texture);
                textureView.Name = $"ImGui.NET {texture.Name} View";
                _autoViewsByTexture.Add(texture, textureView);
                _ownedResources.Add(textureView);
            }
            return GetOrCreateImGuiBinding(factory, textureView);
        }

        public void RemoveImGuiBinding(Texture texture)
        {
            if (_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                _autoViewsByTexture.Remove(texture);
                _ownedResources.Remove(textureView);
                RemoveImGuiBinding(textureView);
                textureView.Dispose();
            }
        }

        /// <summary>Retrieves the texture view for the given helper handle.</summary>
        public TextureView GetImageTextureView(IntPtr imGuiBinding)
        {
            if (!_viewsById.TryGetValue(imGuiBinding, out TextureView view))
            {
                throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
            }
            return view;
        }

        public void ClearCachedImageResources()
        {
            foreach (var view in _idsByView.Keys) _shape.FontTexture.Forget(view);
            foreach (IDisposable resource in _ownedResources)
            {
                resource.Dispose();
            }
            _ownedResources.Clear();
            _idsByView.Clear();
            _viewsById.Clear();
            _autoViewsByTexture.Clear();
            _lastAssignedID = 100;
        }

        private string GetEmbeddedResourceText(string resourceName)
        {
            using (Stream s = _assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException($"embedded resource '{resourceName}' not found"))
            using (StreamReader sr = new StreamReader(s))
            {
                return sr.ReadToEnd();
            }
        }

        /// <summary>Recreates the device texture used to render text.</summary>
        public unsafe void RecreateFontDeviceTexture() => RecreateFontDeviceTexture(_gd);

        /// <summary>Recreates the device texture used to render text.</summary>
        public unsafe void RecreateFontDeviceTexture(GraphicsDevice gd)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
            io.Fonts.SetTexID(_fontAtlasID);

            if (_fontTexture != null)
            {
                _shape.FontTexture.Forget(_fontTexture);
                _fontTexture.Dispose();
            }
            _fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            _fontTexture.Name = "ImGui.NET Font Texture";
            gd.UpdateTexture(_fontTexture, (IntPtr)pixels, (uint)(bytesPerPixel * width * height), 0, 0, 0, (uint)width, (uint)height, 1, 0, 0);

            io.Fonts.ClearTexData();
        }

        /// <summary>Renders the ImGui draw list data.</summary>
        public unsafe void Render(GraphicsDevice gd, CommandList cl)
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData(), gd, cl);
            }
        }

        /// <summary>
        /// Begins a new ImGui frame: renders (discards) the previous frame if it was never passed to
        /// <see cref="Render(GraphicsDevice, CommandList)"/>, then sets DisplaySize, DisplayFramebufferScale and DeltaTime.
        /// Call once per frame <b>before</b> feeding input via the <c>Add*Event</c> methods.
        /// </summary>
        public void BeginFrame(float deltaSeconds)
        {
            if (_frameBegun)
            {
                ImGui.Render();
            }
            SetPerFrameImGuiData(deltaSeconds);
        }

        /// <summary>Ends input feeding and calls <c>ImGui.NewFrame()</c>. Call once per frame <b>after</b> feeding input.</summary>
        public void EndFrame()
        {
            _frameBegun = true;
            ImGui.NewFrame();
        }

        private unsafe void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(_windowWidth / _scaleFactor.X, _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds;
        }

        private unsafe void RenderImDrawData(ImDrawDataPtr draw_data, GraphicsDevice gd, CommandList cl)
        {
            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            uint totalVertices = checked((uint)draw_data.TotalVtxCount);
            if (totalVertices > _vertexBuffer.Capacity)
            {
                _vertexBuffer.Dispose();
                _vertexBuffer = _shape.Vertices.CreateBuffer(checked(totalVertices + totalVertices / 2));
            }

            uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > _indexBuffer.SizeInBytes)
            {
                _indexBuffer.Dispose();
                _indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _indexBuffer.Name = $"ImGui.NET Index Buffer";
            }

            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[i];
                // ImGui is an external data producer: explicitly map its named fields rather than reinterpret its native byte layout.
                int count = cmd_list.VtxBuffer.Size;
                var vertices = System.Buffers.ArrayPool<ImGuiVertex>.Shared.Rent(count);
                try
                {
                    for (int v = 0; v < count; v++)
                    {
                        var source = cmd_list.VtxBuffer[v];
                        vertices[v] = new ImGuiVertex
                        {
                            vsin_position = source.pos,
                            vsin_texCoord = source.uv,
                            vsin_color = source.col,
                        };
                    }
                    _vertexBuffer.Update(cl, vertices.AsSpan(0, count), vertexOffsetInVertices);
                }
                finally { System.Buffers.ArrayPool<ImGuiVertex>.Shared.Return(vertices); }
                cl.UpdateBuffer(_indexBuffer, indexOffsetInElements * sizeof(ushort), cmd_list.IdxBuffer.Data, (uint)(cmd_list.IdxBuffer.Size * sizeof(ushort)));
                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            // Orthographic projection into the shape's uniform block (typed; the block and its member were verified at Bind).
            {
                var io = ImGui.GetIO();
                Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0.0f, -1.0f, 1.0f);
                _shape.Projection.Update(cl, new ImGuiProjection
                {
                    projection = mvp,
                    IsClipSpaceYInverted = gd.IsClipSpaceYInverted ? 1 : 0,
                    UseLegacyColorSpaceHandling = _colorSpaceHandling == ColorSpaceHandling.Legacy ? 1 : 0,
                });
            }

            cl.SetPipeline(_pipeline);
            _shape.Vertices.Bind(cl, _vertexBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);

            draw_data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }

                    if (pcmd.TextureId != IntPtr.Zero)
                    {
                        if (pcmd.TextureId == _fontAtlasID) _shape.FontTexture.Set(_fontTexture);
                        else _shape.FontTexture.Set(GetImageTextureView(pcmd.TextureId));
                    }
                    _shape.BindResourceSets(cl);   // cached per distinct texture; free after first use

                    cl.SetScissorRect(0, (uint)pcmd.ClipRect.X, (uint)pcmd.ClipRect.Y, (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X), (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));
                    cl.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)idx_offset, (int)(pcmd.VtxOffset + vtx_offset), 0);
                }
                idx_offset += cmd_list.IdxBuffer.Size;
                vtx_offset += cmd_list.VtxBuffer.Size;
            }
        }

        /// <summary>Frees all graphics resources used by the renderer.</summary>
        public void Dispose()
        {
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            _fontTexture.Dispose();
            _pipeline.Dispose();
            _shape.Dispose();
            foreach (Shader shader in _bundle.Shaders) shader.Dispose();
            foreach (IDisposable resource in _ownedResources)
            {
                resource.Dispose();
            }
        }
    }
}