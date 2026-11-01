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

    /// <summary>The Projection block of ImGui.vert.glsl, including cross-backend runtime flags. 80 B = the 16-rounded std140 block size (64 + 4 + 4 -> 72 -> 80).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    public struct ImGuiProjection
    {
        public Matrix4x4 projection;
        public int IsClipSpaceYInverted;
        public int UseLegacyColorSpaceHandling;
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