using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

public sealed class ImGuiBundleContractTests
{
    [Fact]
    public void CommittedImGuiBundleMatchesActualShape()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContractTests.ImGui.vdshader");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var bundle = VeldridShaderBundle.Deserialize(reader.ReadToEnd());
        var violations = ShaderPipelineShape.Verify<ImGuiShape>(bundle);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
        Assert.Equal(20, Marshal.SizeOf<ImGuiVertex>());
        Assert.Equal(16, Marshal.OffsetOf<ImGuiVertex>(nameof(ImGuiVertex.vsin_color)).ToInt32());
        Assert.Equal(80, Marshal.SizeOf<ImGuiProjection>());
        Assert.Equal(64, Marshal.OffsetOf<ImGuiProjection>(nameof(ImGuiProjection.IsClipSpaceYInverted)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<ImGuiProjection>(nameof(ImGuiProjection.UseLegacyColorSpaceHandling)).ToInt32());
        Assert.Equal(new[] { "vsin_position", "vsin_texCoord", "vsin_color" }, bundle.Interface.VertexInputs.Select(i => i.Name));
    }
}