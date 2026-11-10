using System;
using System.Diagnostics;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     Contract of the two GPU synchronisation verbs (<c>_TASKS/GpuOrderingBarrier_vs_WaitForGpuIdle.md</c> section 5): GpuOrderingBarrier()
///     orders GPU work (cheap, may not wait); WaitForGpuIdle() blocks the CPU until the GPU has finished everything; Fence reflects GPU completion
///     of the submission it was attached to. Serial with the assembly (TestParallelism.cs).
/// </summary>
public sealed class GpuSyncVerbTests
{
    public static TheoryData<GraphicsBackend> Backends => new() { GraphicsBackend.Direct3D11, GraphicsBackend.Vulkan };

    private static GraphicsDevice TryCreate(GraphicsBackend backend)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ShapeTestHarness.CreateDevice(backend); } catch (Exception) { return null; }
    }

    [Theory, MemberData(nameof(Backends))]
    public void WaitForGpuIdle_ThenTimestampsAreReadyOnFirstResolve(GraphicsBackend backend)
    {
        using var device = TryCreate(backend);
        if (device == null || !device.Features.TimestampQueries) return;
        using var scene = new TimestampProbe_DrawScene(device, targetSize: 2048);
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        scene.BeginPassAndDraw(cl);
        cl.WriteTimestamp(pool, 0);
        scene.DrawFullTargetTriangles(cl, 64);   // tens of ms of fill: the GPU is certainly still busy when SubmitCommands returns
        cl.WriteTimestamp(pool, 1);
        cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();

        Span<ulong> ticks = stackalloc ulong[2];
        Assert.Equal(TimestampResolveResult.Ready, device.TryResolveTimestamps(pool, 0, 2, ticks, out _));   // FIRST call, no polling
    }

    [Theory, MemberData(nameof(Backends))]
    public void GpuOrderingBarrier_ReadbackObservesPriorWork(GraphicsBackend backend)
    {
        using var device = TryCreate(backend);
        if (device == null) return;
        uint[] payload = new uint[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = 0xA5000000u | (uint)i;
        using var source = device.ResourceFactory.CreateBuffer(new BufferDescription(1024, BufferUsage.VertexBuffer));
        using var readback = device.ResourceFactory.CreateBuffer(new BufferDescription(1024, BufferUsage.Staging));
        using var cl = device.ResourceFactory.CreateCommandList();
        cl.Begin(); cl.UpdateBuffer(source, 0, payload); cl.CopyBuffer(source, 0, readback, 0, 1024); cl.End();
        device.SubmitCommands(cl);

        device.GpuOrderingBarrier();   // the ONLY promise: the Map below observes the copy. D3D11 does nothing here and must still pass.
        var view = device.Map<uint>(readback, MapMode.Read);
        try { for (int i = 0; i < payload.Length; i++) Assert.Equal(payload[i], view[i]); }
        finally { device.Unmap(readback); }
    }

    [Theory, MemberData(nameof(Backends))]
    public void Fence_NotSignaledImmediatelyAfterHeavySubmit_SignaledAfterWaitForFence(GraphicsBackend backend)
    {
        using var device = TryCreate(backend);
        if (device == null) return;
        using var scene = new TimestampProbe_DrawScene(device, targetSize: 2048);
        using var fence = device.ResourceFactory.CreateFence(signaled: false);
        using var cl = device.ResourceFactory.CreateCommandList();
        Assert.False(fence.Signaled);

        cl.Begin();
        scene.BeginPassAndDraw(cl);
        scene.DrawFullTargetTriangles(cl, 128);   // ~100+ ms of fill on a 4080-class GPU; far longer than the submit call
        cl.End();
        var sw = Stopwatch.StartNew();
        device.SubmitCommands(cl, fence);
        bool signaledRightAway = fence.Signaled;
        sw.Stop();

        // D3D11 used to set a ManualResetEvent inside SubmitCommands, so this was ALWAYS true there. If the submit itself took suspiciously long
        // (driver stalled us) the observation is not meaningful; only assert when the submit returned promptly.
        if (sw.ElapsedMilliseconds < 20) Assert.False(signaledRightAway, $"{backend}: Fence must not be signaled {sw.ElapsedMilliseconds} ms after submitting ~100 ms of GPU work");

        Assert.True(device.WaitForFence(fence, TimeSpan.FromSeconds(10)), "WaitForFence must return true once the GPU finished");
        Assert.True(fence.Signaled);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Fence_WaitForFenceTimeout_ReturnsFalseWhileGpuBusy_ThenTrue(GraphicsBackend backend)
    {
        using var device = TryCreate(backend);
        if (device == null) return;
        using var scene = new TimestampProbe_DrawScene(device, targetSize: 2048);
        using var fence = device.ResourceFactory.CreateFence(signaled: false);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        scene.BeginPassAndDraw(cl);
        scene.DrawFullTargetTriangles(cl, 128);
        cl.End();
        device.SubmitCommands(cl, fence);

        bool withinOneNanosecond = device.WaitForFence(fence, nanosecondTimeout: 1);
        Assert.False(withinOneNanosecond, $"{backend}: a 1 ns WaitForFence right after submitting ~100 ms of GPU work must time out");
        Assert.True(device.WaitForFence(fence, TimeSpan.FromSeconds(10)));

        // Reset returns it to unsignaled; a fresh submit signals it again (EVENT query re-issue is the canonical cycle on D3D11).
        fence.Reset();
        Assert.False(fence.Signaled);
        cl.Begin(); cl.End();
        device.SubmitCommands(cl, fence);
        Assert.True(device.WaitForFence(fence, TimeSpan.FromSeconds(10)));
    }

    [Theory, MemberData(nameof(Backends))]
    public void WaitForIdle_Obsolete_StillRunsAsGpuOrderingBarrier(GraphicsBackend backend)
    {
        using var device = TryCreate(backend);
        if (device == null) return;
#pragma warning disable CS0618 // the point of the test is that the obsolete alias keeps working
        device.WaitForIdle();
#pragma warning restore CS0618
        device.GpuOrderingBarrier();
    }
}