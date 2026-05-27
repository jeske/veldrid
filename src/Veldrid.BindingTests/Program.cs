using System;
using System.Collections.Generic;
using System.Linq;
using Veldrid;
using Veldrid.D3D11;

namespace Veldrid.BindingTests;

/// <summary>
/// Integration test for D3D11 shader binding validation.
/// Creates a real D3D11 device and verifies that CreateFromBundle() + CreateResourceLayouts()
/// produces ResourceLayouts with correct slot assignments matching the flat binding map.
/// </summary>
internal static class Program
{
    private static int passCount;
    private static int failCount;
    private static readonly List<string> failures = new();

    static int Main(string[] args)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("  Veldrid D3D11 Binding Validation Tests");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();

        GraphicsDevice gd;
        try
        {
            gd = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions());
            Console.WriteLine($"Created D3D11 device: {gd.DeviceName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKIP: Cannot create D3D11 device: {ex.Message}");
            return 0; // Not a failure — just no D3D11 available
        }

        Console.WriteLine();

        try
        {
            Test_SingleSet_SingleCB(gd);
            Test_TwoSets_CBAndTexture(gd);
            Test_ThreeSets_MixedResources(gd);
            Test_ExplicitBindings_SkipBaseOffset(gd);
            Test_OldPath_UsesSequentialCounters(gd);
        }
        finally
        {
            gd.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine($"  Results: {passCount} passed, {failCount} failed");
        Console.WriteLine("═══════════════════════════════════════════════════════════");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FAILURES:");
            foreach (var f in failures)
                Console.WriteLine($"  ✗ {f}");
        }

        return failCount > 0 ? 1 : 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test: Single set with a single uniform buffer
    // Bundle says: set=0, binding=0, kind=UniformBuffer, flatIndex=0
    // Expected: D3D11 slot = 0 (b0)
    // ─────────────────────────────────────────────────────────────────────
    static void Test_SingleSet_SingleCB(GraphicsDevice gd)
    {
        var entries = new VdShaderBindingEntry[]
        {
            new() { Name = "ViewBuf", Set = 0, Binding = 0, Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex, FlatIndex = 0 }
        };

        var layoutDesc = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ViewBuf", ResourceKind.UniformBuffer, ShaderStages.Vertex));

        var layout = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(
            ref layoutDesc, 0, entries);

        AssertSlot("SingleSet_SingleCB: ViewBuf", layout, 0, expectedSlot: 0);
        AssertExplicit("SingleSet_SingleCB", layout, expectedExplicit: true);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test: Two sets — set 0 has a CB, set 1 has a texture + sampler
    // Bundle says: set0/binding0 = b0, set1/binding0 = t2, set1/binding1 = s2
    // Key: The flatIndex for set 1 is NOT 0 — it's 2 (global register)
    // ─────────────────────────────────────────────────────────────────────
    static void Test_TwoSets_CBAndTexture(GraphicsDevice gd)
    {
        // Set 0: one CB at b0
        var entries0 = new VdShaderBindingEntry[]
        {
            new() { Name = "ViewBuf", Set = 0, Binding = 0, Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex, FlatIndex = 0 }
        };
        var desc0 = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ViewBuf", ResourceKind.UniformBuffer, ShaderStages.Vertex));
        var layout0 = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(
            ref desc0, 0, entries0);

        // Set 1: texture at t2, sampler at s2 (NOT t0/s0!)
        var entries1 = new VdShaderBindingEntry[]
        {
            new() { Name = "Heightmap", Set = 1, Binding = 0, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Vertex, FlatIndex = 2 },
            new() { Name = "HeightmapSampler", Set = 1, Binding = 1, Kind = ResourceKind.Sampler, Stages = ShaderStages.Vertex, FlatIndex = 2 }
        };
        var desc1 = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Heightmap", ResourceKind.TextureReadOnly, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("HeightmapSampler", ResourceKind.Sampler, ShaderStages.Vertex));
        var layout1 = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(
            ref desc1, 1, entries1);

        AssertSlot("TwoSets: ViewBuf", layout0, 0, expectedSlot: 0);
        AssertSlot("TwoSets: Heightmap", layout1, 0, expectedSlot: 2);
        AssertSlot("TwoSets: HeightmapSampler", layout1, 1, expectedSlot: 2);
        AssertExplicit("TwoSets: layout0", layout0, expectedExplicit: true);
        AssertExplicit("TwoSets: layout1", layout1, expectedExplicit: true);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test: Three sets with mixed resource types — simulates AN_Monsters heightmap shader
    // Set 0: CB at b0 (vertex), CB at b1 (fragment)
    // Set 1: CB at b2 (fragment)
    // Set 2: Tex t0 (vertex heightmap), Tex t1 (frag surface), Tex t2 (frag biome),
    //         Tex t3 (frag color), Sampler s0, Sampler s1
    // ─────────────────────────────────────────────────────────────────────
    static void Test_ThreeSets_MixedResources(GraphicsDevice gd)
    {
        // Set 0
        var entries0 = new VdShaderBindingEntry[]
        {
            new() { Name = "ViewProj", Set = 0, Binding = 0, Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Vertex, FlatIndex = 0 },
            new() { Name = "FragParams", Set = 0, Binding = 1, Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Fragment, FlatIndex = 1 }
        };
        var desc0 = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ViewProj", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("FragParams", ResourceKind.UniformBuffer, ShaderStages.Fragment));
        var layout0 = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(ref desc0, 0, entries0);

        // Set 1
        var entries1 = new VdShaderBindingEntry[]
        {
            new() { Name = "Material", Set = 1, Binding = 0, Kind = ResourceKind.UniformBuffer, Stages = ShaderStages.Fragment, FlatIndex = 2 }
        };
        var desc1 = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Material", ResourceKind.UniformBuffer, ShaderStages.Fragment));
        var layout1 = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(ref desc1, 1, entries1);

        // Set 2 — textures and samplers
        var entries2 = new VdShaderBindingEntry[]
        {
            new() { Name = "Heightmap",    Set = 2, Binding = 0, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Vertex,   FlatIndex = 0 },
            new() { Name = "SurfaceType",  Set = 2, Binding = 1, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment, FlatIndex = 1 },
            new() { Name = "BiomeArray",   Set = 2, Binding = 2, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment, FlatIndex = 2 },
            new() { Name = "ColorAtlas",   Set = 2, Binding = 3, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment, FlatIndex = 3 },
            new() { Name = "HeightSampler", Set = 2, Binding = 4, Kind = ResourceKind.Sampler,        Stages = ShaderStages.Vertex,   FlatIndex = 0 },
            new() { Name = "TexSampler",   Set = 2, Binding = 5, Kind = ResourceKind.Sampler,        Stages = ShaderStages.Fragment, FlatIndex = 1 }
        };
        var desc2 = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Heightmap",     ResourceKind.TextureReadOnly, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("SurfaceType",   ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("BiomeArray",    ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("ColorAtlas",    ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("HeightSampler", ResourceKind.Sampler,         ShaderStages.Vertex),
            new ResourceLayoutElementDescription("TexSampler",    ResourceKind.Sampler,         ShaderStages.Fragment));
        var layout2 = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(ref desc2, 2, entries2);

        // Set 0 assertions
        AssertSlot("ThreeSets: ViewProj",   layout0, 0, expectedSlot: 0);
        AssertSlot("ThreeSets: FragParams", layout0, 1, expectedSlot: 1);

        // Set 1 assertions
        AssertSlot("ThreeSets: Material",   layout1, 0, expectedSlot: 2);

        // Set 2 assertions — textures
        AssertSlot("ThreeSets: Heightmap",   layout2, 0, expectedSlot: 0);
        AssertSlot("ThreeSets: SurfaceType", layout2, 1, expectedSlot: 1);
        AssertSlot("ThreeSets: BiomeArray",  layout2, 2, expectedSlot: 2);
        AssertSlot("ThreeSets: ColorAtlas",  layout2, 3, expectedSlot: 3);

        // Set 2 assertions — samplers
        AssertSlot("ThreeSets: HeightSampler", layout2, 4, expectedSlot: 0);
        AssertSlot("ThreeSets: TexSampler",    layout2, 5, expectedSlot: 1);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test: Verify that explicit bindings result in base offset = 0
    // When HasExplicitBindingSlots is true, the command list should NOT
    // add cross-set offsets. We verify this by checking that a layout in
    // set 2 still reports its raw flatIndex slots (not offset by set 0+1 counts).
    // ─────────────────────────────────────────────────────────────────────
    static void Test_ExplicitBindings_SkipBaseOffset(GraphicsDevice gd)
    {
        // This test validates the logical contract: explicit layouts report
        // absolute slots. The actual base-offset skip is in the command list
        // (tested via the runtime diagnostic output).
        // Here we just confirm the layout's slot IS the flatIndex, not sequential.

        var entries = new VdShaderBindingEntry[]
        {
            new() { Name = "Tex", Set = 2, Binding = 0, Kind = ResourceKind.TextureReadOnly, Stages = ShaderStages.Fragment, FlatIndex = 5 },
        };
        var desc = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment));
        var layout = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(ref desc, 2, entries);

        // If base offset were applied, this would be wrong (it would get offset by set 0+1 tex counts)
        // But the layout itself just stores 5 — the command list is what skips the offset.
        AssertSlot("ExplicitSkipBase: Tex in set 2", layout, 0, expectedSlot: 5);
        AssertExplicit("ExplicitSkipBase", layout, expectedExplicit: true);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test: Old path (no binding entries) still uses sequential counters
    // This confirms the legacy behavior hasn't regressed.
    // ─────────────────────────────────────────────────────────────────────
    static void Test_OldPath_UsesSequentialCounters(GraphicsDevice gd)
    {
        var desc = new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("CB1", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("Tex1", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("CB2", ResourceKind.UniformBuffer, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("Tex2", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("Samp1", ResourceKind.Sampler, ShaderStages.Fragment));

        var layout = (D3D11ResourceLayout)gd.ResourceFactory.CreateResourceLayout(ref desc);

        // Sequential per-type: CB gets 0,1; Tex gets 0,1; Sampler gets 0
        AssertSlot("OldPath: CB1",   layout, 0, expectedSlot: 0);
        AssertSlot("OldPath: Tex1",  layout, 1, expectedSlot: 0);
        AssertSlot("OldPath: CB2",   layout, 2, expectedSlot: 1);
        AssertSlot("OldPath: Tex2",  layout, 3, expectedSlot: 1);
        AssertSlot("OldPath: Samp1", layout, 4, expectedSlot: 0);
        AssertExplicit("OldPath", layout, expectedExplicit: false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Assertion Helpers
    // ─────────────────────────────────────────────────────────────────────

    static void AssertSlot(string testName, D3D11ResourceLayout layout, int elementIndex, int expectedSlot)
    {
        var rbi = layout.GetDeviceSlotIndex(elementIndex);
        if (rbi.Slot == expectedSlot)
        {
            Console.WriteLine($"  ✓ {testName}: slot={rbi.Slot} (expected {expectedSlot})");
            passCount++;
        }
        else
        {
            Console.WriteLine($"  ✗ {testName}: slot={rbi.Slot} (EXPECTED {expectedSlot})");
            failCount++;
            failures.Add($"{testName}: got slot={rbi.Slot}, expected={expectedSlot}");
        }
    }

    static void AssertExplicit(string testName, D3D11ResourceLayout layout, bool expectedExplicit)
    {
        if (layout.HasExplicitBindingSlots == expectedExplicit)
        {
            Console.WriteLine($"  ✓ {testName}: HasExplicitBindingSlots={layout.HasExplicitBindingSlots}");
            passCount++;
        }
        else
        {
            Console.WriteLine($"  ✗ {testName}: HasExplicitBindingSlots={layout.HasExplicitBindingSlots} (EXPECTED {expectedExplicit})");
            failCount++;
            failures.Add($"{testName}: HasExplicitBindingSlots={layout.HasExplicitBindingSlots}, expected={expectedExplicit}");
        }
    }
}