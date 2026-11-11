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
    public void Auto_TrackerCapReached_DropsNeverBlocks(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.MaxTrackedCommandLists = 2;        // exactly 2 recordings may be in flight with timestamps
        device.GpuTiming.MaxDebugGroupsPerCommandList = 0;
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;
        using var cl = device.ResourceFactory.CreateCommandList();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            cl.Begin(); workload.Record(cl, 1); cl.End();
            device.SubmitCommands(cl);   // never drained in between -> both trackers are pending after 2
        }
        sw.Stop();

        Assert.Equal(8UL, device.GpuTiming.DroppedCommandLists);
        Assert.True(sw.Elapsed.TotalSeconds < 5, "recording at the tracker cap must not block");

        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 2);
        Assert.Equal(2, cls.Count);

        // The trackers are back in the pool: the next command list is traced.
        cl.Begin(); workload.Record(cl, 1); cl.End();
        device.SubmitCommands(cl);
        DrainWithRetry(device, cls, groups, expectedCommandLists: 3);
        Assert.Equal(8UL, device.GpuTiming.DroppedCommandLists);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_BeginWithoutSubmit_IsAbandonedAndTrackerRecovers(GraphicsBackend backend)
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

        // The abandoned slots were discarded -> reset by the tracker's next owner at its Begin -> usable inside a pass again (no throw) when the tracker is reused.
        using var scene = new TimestampProbe_DrawScene(device);
        device.GpuTiming.ResetTrackerPool();
        device.GpuTiming.MaxTrackedCommandLists = 2; device.GpuTiming.MaxDebugGroupsPerCommandList = 1;   // 2 trackers of 4 slots: every tracker is reused every 2 lists
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        for (int i = 0; i < 6; i++)
        {
            cl.Begin(); scene.BeginPassAndDraw(cl); cl.PushDebugGroup("g"); scene.Draw(cl); cl.PopDebugGroup(); cl.End();
            if (i % 3 == 2) { cl.Begin(); cl.End(); }  // abandon the recording every third lap (Begin again, no submit of the previous)
            device.SubmitCommands(cl);
            DrainWithRetry(device, cls, groups, expectedCommandLists: cls.Count + 1);
        }
    }

    // ---- section 10 (pooled allocator, Nov 10): the orderings the ring could not survive ----

    [Theory, MemberData(nameof(Backends))]
    public void Auto_OutOfOrderSubmit_NeverThrows(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        var lists = new CommandList[3];
        for (int i = 0; i < lists.Length; i++) { lists[i] = device.ResourceFactory.CreateCommandList(); lists[i].Name = $"OoO.CL{i}"; }

        // Begin order 0,1,2 - Submit order 2,1,0, every iteration. The ring allocated by Begin position and freed by Submit order: within one wrap of
        // this pattern it reissued a Written slot and threw. The pool does not care which order anything happens in. GPU-PACED: a 64 MB copy per list
        // outruns the GPU within a few dozen iterations and legitimately hits the ~120-tracker cap (drop, never block) - which is not what this test is about.
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        const int iterations = 200;
        for (int it = 0; it < iterations; it++)
        {
            foreach (var cl in lists)
            {
                cl.Begin();
                for (int g = 0; g < 4; g++) { cl.PushDebugGroup($"g{g}"); workload.Record(cl, 1); cl.PopDebugGroup(); }
                cl.End();
            }
            for (int i = lists.Length - 1; i >= 0; i--) device.SubmitCommands(lists[i]);
            DrainWithRetry(device, cls, groups, expectedCommandLists: (it + 1) * 3);
        }
        DrainWithRetry(device, cls, groups, expectedCommandLists: iterations * 3, expectedGroups: iterations * 12);

        Assert.True(cls.Count == iterations * 3, $"spans {cls.Count}/{iterations * 3}, dropped {device.GpuTiming.DroppedCommandLists}, live {device.GpuTiming.LiveBlocks}");
        Assert.Equal(iterations * 12, groups.Count);
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
        Assert.True(device.GpuTiming.LiveBlocks <= 3 + 1, $"three recorders need at most three trackers in flight (live {device.GpuTiming.LiveBlocks})");
        foreach (var cl in lists) cl.Dispose();
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_HeldList_DoesNotBlackOut(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;
        using var held = device.ResourceFactory.CreateCommandList();
        using var worker = device.ResourceFactory.CreateCommandList();
        held.Name = "Held"; worker.Name = "Worker";

        // A list Begin()'d and never submitted for the whole run (submitted next Tuesday). With the ring, its block eventually sat under the allocation head
        // and every other recording was dropped (or threw) for as long as it was held. With the pool it pins exactly one tracker. GPU-paced (see Auto_OutOfOrderSubmit):
        // the point is "a held list never blocks others", not "the cap is infinite".
        held.Begin(); workload.Record(held, 1);
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        const int submits = 300;   // more laps than the ~120-tracker cap, so a ring would have met the held block under its head
        for (int i = 0; i < submits; i++)
        {
            worker.Begin(); workload.Record(worker, 1); worker.End();
            device.SubmitCommands(worker);
            DrainWithRetry(device, cls, groups, expectedCommandLists: i + 1);
        }
        DrainWithRetry(device, cls, groups, expectedCommandLists: submits);

        Assert.True(cls.Count == submits, $"spans {cls.Count}/{submits}, dropped {device.GpuTiming.DroppedCommandLists}, live {device.GpuTiming.LiveBlocks}");
        Assert.All(cls, s => Assert.Equal("Worker", s.CommandListName));
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
        held.End();   // release it at the end so the device disposes cleanly; never submitted -> no span
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_LostList_IsFinalizedAndRepooled(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;

        LoseARecordingList(device, workload);   // recorded, never submitted, never disposed: every reference dropped on return - the list is simply lost
        Assert.Equal(1, device.GpuTiming.LiveBlocks);
        // Full system GC: the lost CommandList, its record and its tracker are all unreachable; the tracker's finalizer runs here and must do nothing but report.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // The tracker's finalizer reported to the profiler and did nothing else; the next Begin() drains the report and RE-POOLS that tracker:
        // no new block is created (LiveBlocks stays 1), and the recording works.
        using var cl = device.ResourceFactory.CreateCommandList();
        cl.Begin(); workload.Record(cl, 1); cl.End();
        device.SubmitCommands(cl);
        Assert.Equal(1UL, device.GpuTiming.FinalizedBlocks);
        Assert.Equal(1, device.GpuTiming.LiveBlocks);
        Assert.Null(device.GpuTiming.Fault);
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();
        DrainWithRetry(device, cls, groups, expectedCommandLists: 1);
        Assert.Single(cls);
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void LoseARecordingList(GraphicsDevice device, TimestampProbe_Workload workload)
    {
        var lost = device.ResourceFactory.CreateCommandList();
        lost.Begin(); workload.Record(lost, 1); lost.End();
        // Deliberately NOT disposed: the whole point is a list dropped on the floor. Veldrid command lists have no finalizer of their own, so the only
        // finalizer that runs for this object graph is the tracker's - and it must talk to nobody but the profiler.
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_PendingResolveTimeout_ReclaimsTrackerNeverThrows(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.MaxTrackedCommandLists = 1;
        device.GpuTiming.PendingResolveTimeout = TimeSpan.Zero;   // every head that is not ready on its FIRST drain is written off as lost
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists;
        using var cl = device.ResourceFactory.CreateCommandList();
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();

        // A dead head (device that stopped completing work) cannot be provoked on a healthy GPU, so the timeout is driven to zero instead: with ONE tracker,
        // each submit's record is either resolved or timed out on the very next drain - and either way the tracker comes back so the NEXT recording is traced.
        // A pool that did not reclaim would drop every recording after the first unresolved one.
        const int submits = 50;
        for (int i = 0; i < submits; i++)
        {
            cl.Begin(); workload.Record(cl, 1); cl.End();
            device.SubmitCommands(cl);
            device.GpuTiming.DrainCompleted(cls, groups);
        }
        device.WaitForGpuIdle();
        device.GpuTiming.DrainCompleted(cls, groups);

        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
        Assert.Equal((ulong)submits, (ulong)cls.Count + device.GpuTiming.TimedOutCommandLists);
        Assert.Equal(1, device.GpuTiming.LiveBlocks);
        Assert.Null(device.GpuTiming.Fault);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Auto_IdleThenBurst_NeverThrows(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        device.GpuTiming.Mode = GpuTiming_Mode.CommandLists | GpuTiming_Mode.DebugGroups;
        using var cl = device.ResourceFactory.CreateCommandList();
        var cls = new List<GpuTiming_CommandListSpan>();
        var groups = new List<GpuTiming_DebugGroupSpan>();

        // The AN_Monsters crash shape: a few timed lists, a long GPU idle (no submits, no drains), then a burst longer than one old ring wrap. GPU-paced (see Auto_OutOfOrderSubmit).
        for (int i = 0; i < 10; i++) { cl.Begin(); cl.PushDebugGroup("p"); workload.Record(cl, 1); cl.PopDebugGroup(); cl.End(); device.SubmitCommands(cl); }
        System.Threading.Thread.Sleep(2000);
        for (int i = 0; i < 200; i++) { cl.Begin(); cl.PushDebugGroup("p"); workload.Record(cl, 1); cl.PopDebugGroup(); cl.End(); device.SubmitCommands(cl); DrainWithRetry(device, cls, groups, expectedCommandLists: 10 + i + 1); }
        DrainWithRetry(device, cls, groups, expectedCommandLists: 210, expectedGroups: 210);
        Assert.True(cls.Count == 210, $"spans {cls.Count}/210, dropped {device.GpuTiming.DroppedCommandLists}, live {device.GpuTiming.LiveBlocks}");
        Assert.Equal(0UL, device.GpuTiming.DroppedCommandLists);
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