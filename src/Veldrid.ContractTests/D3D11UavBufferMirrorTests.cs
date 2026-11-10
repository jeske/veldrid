using System;
using Veldrid;
using Veldrid.D3D11;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     _BUGFIX/BUGFIX_D3D11_ComputeUavBindingList_Grows_PerFrame.md: the D3D11 command list mirrors which BUFFERS are bound as UAVs
///     (so a later vertex / index / SRV bind of the same buffer can unset the UAV first). That mirror must be (a) bounded by the UAV
///     slot count within one command list - one entry per slot, re-binding replaces - and (b) empty after <c>End</c> / <c>Begin</c>.
///     Before the fix it was appended to unconditionally and never cleared, so one compute dispatch per frame made every later
///     <c>SetVertexBuffer</c> scan a list thousands long (60 -> 30 fps in 40 s in AN_Monsters).
///     Exercised through the internal seam (no compute .vdshader bundle is available to these tests); the seam calls the same private
///     tracking path a resource-set bind does.
/// </summary>
public class D3D11UavBufferMirrorTests
{
    private const int uav_buffers_per_dispatch = 4;
    private const int simulated_frames = 1000;

    private static GraphicsDevice TryCreateD3D11()
    {
        try { return GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions()); }
        catch (Exception) { return null; }   // no D3D11 device here: nothing to assert
    }

    private static DeviceBuffer[] CreateUavBuffers(ResourceFactory factory)
    {
        var buffers = new DeviceBuffer[uav_buffers_per_dispatch];
        for (int i = 0; i < buffers.Length; i++)
            buffers[i] = factory.CreateBuffer(new BufferDescription(256, BufferUsage.StructuredBufferReadWrite, 16));
        return buffers;
    }

    [Fact]
    public void RebindingTheSameSlots_DoesNotGrowTheMirror()
    {
        using var device = TryCreateD3D11();
        if (device == null) return;

        var buffers = CreateUavBuffers(device.ResourceFactory);
        using var commands = (D3D11CommandList)device.ResourceFactory.CreateCommandList();

        commands.Begin();
        for (int frame = 0; frame < simulated_frames; frame++)
            for (int slot = 0; slot < buffers.Length; slot++)
                commands.DebugTrackUavBufferForTests(buffers[slot], slot, compute: true);

        // One entry per slot, however many times the set was re-bound. Previously: simulated_frames * uav_buffers_per_dispatch.
        Assert.Equal(uav_buffers_per_dispatch, commands.DebugTrackedUavBufferCount(compute: true));
        Assert.Equal(0, commands.DebugTrackedUavBufferCount(compute: false));
        commands.End();

        foreach (var b in buffers) b.Dispose();
    }

    [Fact]
    public void RebindingASlotToAnotherBuffer_ReplacesTheEntry()
    {
        using var device = TryCreateD3D11();
        if (device == null) return;

        var buffers = CreateUavBuffers(device.ResourceFactory);
        using var commands = (D3D11CommandList)device.ResourceFactory.CreateCommandList();

        commands.Begin();
        commands.DebugTrackUavBufferForTests(buffers[0], 0, compute: false);
        commands.DebugTrackUavBufferForTests(buffers[1], 0, compute: false);   // slot 0 now holds buffers[1]; buffers[0] must be forgotten
        Assert.Equal(1, commands.DebugTrackedUavBufferCount(compute: false));

        // Compute and graphics (OM) stages are separate UAV namespaces: the same slot on the other stage is a second entry.
        commands.DebugTrackUavBufferForTests(buffers[2], 0, compute: true);
        Assert.Equal(1, commands.DebugTrackedUavBufferCount(compute: true));
        Assert.Equal(1, commands.DebugTrackedUavBufferCount(compute: false));
        commands.End();

        foreach (var b in buffers) b.Dispose();
    }

    [Fact]
    public void EndThenBegin_ClearsBothMirrors()
    {
        using var device = TryCreateD3D11();
        if (device == null) return;

        var buffers = CreateUavBuffers(device.ResourceFactory);
        using var commands = (D3D11CommandList)device.ResourceFactory.CreateCommandList();

        for (int frame = 0; frame < 3; frame++)
        {
            commands.Begin();
            Assert.Equal(0, commands.DebugTrackedUavBufferCount(compute: true));    // Begin resets
            Assert.Equal(0, commands.DebugTrackedUavBufferCount(compute: false));

            for (int slot = 0; slot < buffers.Length; slot++)
            {
                commands.DebugTrackUavBufferForTests(buffers[slot], slot, compute: true);
                commands.DebugTrackUavBufferForTests(buffers[slot], slot, compute: false);
            }

            Assert.Equal(uav_buffers_per_dispatch, commands.DebugTrackedUavBufferCount(compute: true));
            Assert.Equal(uav_buffers_per_dispatch, commands.DebugTrackedUavBufferCount(compute: false));
            commands.End();

            Assert.Equal(0, commands.DebugTrackedUavBufferCount(compute: true));    // End resets
            Assert.Equal(0, commands.DebugTrackedUavBufferCount(compute: false));
        }

        foreach (var b in buffers) b.Dispose();
    }
}