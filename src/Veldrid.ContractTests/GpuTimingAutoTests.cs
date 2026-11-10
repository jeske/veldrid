using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     Real-GPU checks of the automatic timing layer (<c>_TASKS/GpuTimestampQueries.md</c> section 4 / 6.2 Auto_*): every submitted command list
///     and every debug group is bracketed with no app marking. Headless D3D11 + Vulkan.
/// </summary>
// Serial with the whole assembly - see TestParallelism.cs (D3D11 multi-device driver hazard).
public sealed class GpuTimingAutoTests
{
    public static TheoryData<GraphicsBackend> Backends => new() { GraphicsBackend.Direct3D11, GraphicsBackend.Vulkan };

    private static GraphicsDevice TryCreateTimestampDevice(GraphicsBackend backend)
    {
        if (!OperatingSystem.IsWindows()) return null;
        GraphicsDevice device;
        try { device = ShapeTestHarness.CreateDevice(backend); }
        catch (Exception) { return null; }
        if (device.Features.TimestampQueries && device.GpuTiming.Supported) return device;
        device.Dispose();
        return null;
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_CommandListMode_EverySubmitProducesOneSpan(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;

        var lists = new CommandList[3];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i] = device.ResourceFactory.CreateCommandList();
            lists[i].Name = $"Auto.CL{i}";
            lists[i].Begin();
            workload.Record(lists[i], repeats: 1 + i);
            lists[i].End();
            device.SubmitCommands(lists[i]);
        }

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 3);

        Assert.Equal(3, cls.Count);
        Assert.Empty(groups);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal($"Auto.CL{i}", cls[i].CommandListName);
            Assert.Equal((ulong)i, cls[i].Submit.Value);
            Assert.True(cls[i].GpuEndTicks > cls[i].GpuStartTicks, $"CL{i}: end must be after start");
            Assert.True(cls[i].TicksPerSecond > 0);
            if (i > 0) Assert.True(cls[i].GpuStartTicks >= cls[i - 1].GpuEndTicks, $"CL{i} must start after CL{i - 1} ended (same queue, submit order)");
        }

        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
        Assert.Equal(0UL, device.GpuTiming.AbandonedCommandLists);
        foreach (var cl in lists) cl.Dispose();
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_DebugGroupMode_NestedGroupsReportDepthAndContainment(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        using var cl = device.ResourceFactory.CreateCommandList();
        cl.Name = "Auto.Nested";

        cl.Begin();
        cl.PushDebugGroup("Outer");
        workload.Record(cl, 1);
        cl.PushDebugGroup("Inner");
        workload.Record(cl, 2);
        cl.PopDebugGroup();
        workload.Record(cl, 1);
        cl.PopDebugGroup();
        cl.PushDebugGroup("Sibling");
        workload.Record(cl, 1);
        cl.PopDebugGroup();
        cl.End();
        device.SubmitCommands(cl);

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 1);

        var list = Assert.Single(cls);
        Assert.Equal(3, groups.Count);
        var outer = groups.Single(g => g.GroupName == "Outer");
        var inner = groups.Single(g => g.GroupName == "Inner");
        var sibling = groups.Single(g => g.GroupName == "Sibling");
        Assert.Equal(0, outer.Depth);
        Assert.Equal(1, inner.Depth);
        Assert.Equal(0, sibling.Depth);
        Assert.All(groups, g => Assert.Equal(list.Submit, g.ParentSubmit));

        // Containment on the device timeline: inner inside outer inside the command list; sibling after outer.
        Assert.True(inner.GpuStartTicks >= outer.GpuStartTicks && inner.GpuEndTicks <= outer.GpuEndTicks, "Inner must lie inside Outer");
        Assert.True(outer.GpuStartTicks >= list.GpuStartTicks && outer.GpuEndTicks <= list.GpuEndTicks, "Outer must lie inside the command list span");
        Assert.True(sibling.GpuStartTicks >= outer.GpuEndTicks, "Sibling must start after Outer ended");
        Assert.True(inner.GpuEndTicks > inner.GpuStartTicks, "Inner wraps real work");
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_DebugGroupMode_InsideRenderPass_IsLegal(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        using var scene = new TimestampProbe_DrawScene(device);
        using var cl = device.ResourceFactory.CreateCommandList();
        cl.Name = "Auto.InPass";

        // Groups pushed/popped INSIDE an open render pass (draws already issued) across several frames: the group slots are Fresh at every
        // push because the previous frame's slots were resolved and reset at Begin(). This is the per-pass pattern the kit relies on.
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        for (int frame = 0; frame < 4; frame++)
        {
            cl.Begin();
            scene.BeginPassAndDraw(cl);
            cl.PushDebugGroup("Pass.A");
            scene.Draw(cl);
            cl.PopDebugGroup();
            cl.PushDebugGroup("Pass.B");
            scene.Draw(cl);
            cl.PopDebugGroup();
            cl.End();
            device.SubmitCommands(cl);
            DrainWithRetry(device, cls, groups, expectedCommandLists: frame + 1);
        }

        Assert.Equal(4, cls.Count);
        Assert.Equal(8, groups.Count);
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_UnclosedGroup_IsClosedAtEndAndFlagged(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.DebugGroups;   // groups only: no command-list spans
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        cl.PushDebugGroup("Forgotten");
        workload.Record(cl, 1);
        cl.End();   // no PopDebugGroup
        device.SubmitCommands(cl);

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 0, expectedGroups: 1);

        Assert.Empty(cls);
        var g = Assert.Single(groups);
        Assert.Equal("Forgotten (unclosed)", g.GroupName);
        Assert.True(g.GpuEndTicks > g.GpuStartTicks);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_ModeOff_ProducesNothing(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        Assert.Equal(GpuTiming_Mode.Off, device.GpuTiming.Mode);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin(); cl.PushDebugGroup("x"); workload.Record(cl, 1); cl.PopDebugGroup(); cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        device.GpuTiming.DrainCompleted(cls, groups);
        Assert.Empty(cls);
        Assert.Empty(groups);
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_RingFull_DropsNeverBlocks(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.RingSlotPairs = 2;                 // 4 slots
        device.GpuTiming.MaxDebugGroupsPerCommandList = 0;  // 2 slots per command list -> exactly 2 command lists fit
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;
        using var cl = device.ResourceFactory.CreateCommandList();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            cl.Begin(); workload.Record(cl, 1); cl.End();
            device.SubmitCommands(cl);   // never drained in between -> ring fills after 2
        }
        sw.Stop();

        Assert.Equal(8UL, device.GpuTiming.DroppedCommandLists);
        Assert.True(sw.Elapsed.TotalSeconds < 5, "recording with a full ring must not block");

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 2);
        Assert.Equal(2, cls.Count);

        // The ring is free again: the next command list is traced.
        cl.Begin(); workload.Record(cl, 1); cl.End();
        device.SubmitCommands(cl);
        DrainWithRetry(device, cls, groups, expectedCommandLists: 3);
        Assert.Equal(8UL, device.GpuTiming.DroppedCommandLists);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_BeginWithoutSubmit_IsAbandonedAndRingRecovers(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        using var cl = device.ResourceFactory.CreateCommandList();

        // Recorded, ended, never submitted; then recorded again. The first record is abandoned, its slots recycled unread.
        cl.Begin(); cl.PushDebugGroup("lost"); workload.Record(cl, 1); cl.PopDebugGroup(); cl.End();
        cl.Begin(); workload.Record(cl, 1); cl.End();
        device.SubmitCommands(cl);

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 1);
        Assert.Single(cls);
        Assert.Empty(groups);
        Assert.Equal(1UL, device.GpuTiming.AbandonedCommandLists);

        // The abandoned slots were discarded -> reset at the next Begin -> usable inside a pass again (no throw) on the next lap of the ring.
        using var scene = new TimestampProbe_DrawScene(device);
        device.GpuTiming.RecreateRing();
        device.GpuTiming.RingSlotPairs = 4; device.GpuTiming.MaxDebugGroupsPerCommandList = 1;   // 4 slots per CL, 8 total: lap every 2 lists
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        for (int i = 0; i < 6; i++)
        {
            cl.Begin(); scene.BeginPassAndDraw(cl); cl.PushDebugGroup("g"); scene.Draw(cl); cl.PopDebugGroup(); cl.End();
            if (i % 3 == 2) { cl.Begin(); cl.End(); }  // abandon the recording every third lap (Begin again, no submit of the previous)
            device.SubmitCommands(cl);
            DrainWithRetry(device, cls, groups, expectedCommandLists: cls.Count + 1);
        }
    }

    // ---- helpers ----

    /// <summary>Drains until the expected number of command-list spans (and groups) have arrived or 3 s pass. D3D11 WaitForIdle is a no-op in this fork, so poll.</summary>
    private static void DrainWithRetry(GraphicsDevice device, List<GpuTiming_CommandListSpan> cls, List<GpuTiming_DebugGroupSpan> groups, int expectedCommandLists, int expectedGroups = -1)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            device.GpuTiming.DrainCompleted(cls, groups);
            bool clsDone = cls.Count >= expectedCommandLists;
            bool groupsDone = expectedGroups < 0 || groups.Count >= expectedGroups;
            if (clsDone && groupsDone) return;
            System.Threading.Thread.Sleep(1);
        }
    }
}