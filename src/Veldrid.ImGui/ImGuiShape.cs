using System.Numerics;
using System.Runtime.InteropServices;

namespace Veldrid
{
    /// <summary>ImGui's vertex (= ImGuiNET.ImDrawVert: pos, uv, packed colour). Field names are the GLSL <c>in</c> names of ImGui.vert.glsl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImGuiVertex
    {
        public Vector2 vsin_position;
        public Vector2 vsin_texCoord;
        [VertexFormat(VertexElementFormat.Byte4_Norm)] public uint vsin_color;
    }

    /// <summary>The Projection block of ImGui.vert.glsl, including cross-backend runtime flags.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImGuiProjection
    {
        public Matrix4x4 projection;
        public int IsClipSpaceYInverted;
        public int UseLegacyColorSpaceHandling;
        public int _pad0, _pad1;
    }

    /// <summary>
    /// ImGui's pipeline as a shape (D10): field names ARE the GLSL names of Shaders/Source/ImGui.{vert,frag}.glsl, verified against the
    /// embedded ImGui.vdshader at <see cref="ShaderPipelineShape.Bind{TShape}"/>. The renderer binds textures per draw command through
    /// <see cref="FontTexture"/>; the shape caches one resource set per distinct texture.
    /// </summary>
    [ShaderBundle("ImGui")]
    public sealed partial class ImGuiShape : ShaderPipelineShape
    {
        public VertexInput<ImGuiVertex> Vertices;
        public UniformBlock<ImGuiProjection> Projection;
        public SamplerSlot FontSampler;
        public TextureSlot FontTexture;
    }
}