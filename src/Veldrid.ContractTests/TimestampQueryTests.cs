using System;
using System.Diagnostics;
using Veldrid;
using Xunit;

namespace Veldrid.ContractTests;

/// <summary>
///     Real-GPU checks of the explicit timestamp primitive (<c>_TASKS/GpuTimestampQueries.md</c> section 6.2): headless D3D11 + Vulkan devices,
///     no window. Workload is <see cref="TimestampProbe_Workload"/> (N x 64 MB buffer copies) - pure GPU work, linear in N, nothing to compile.
/// </summary>
// Serial with the whole assembly - see TestParallelism.cs (D3D11 multi-device driver hazard).
public sealed class TimestampQueryTests
{
    public static TheoryData<GraphicsBackend> Backends => new() { GraphicsBackend.Direct3D11, GraphicsBackend.Vulkan };

    /// <summary>Creates the device or returns null when the backend / feature is unavailable on this machine (the test then passes vacuously).</summary>
    private static GraphicsDevice TryCreateTimestampDevice(GraphicsBackend backend)
    {
        if (!OperatingSystem.IsWindows()) return null;
        GraphicsDevice device;
        try { device = ShapeTestHarness.CreateDevice(backend); }
        catch (Exception) { return null; }
        if (device.Features.TimestampQueries) return device;
        device.Dispose();
        return null;
    }

    [Theory, MemberData(nameof(Backends))]
    public void Features_TimestampQueries_ReportedTrue(GraphicsBackend backend)
    {
        if (!OperatingSystem.IsWindows()) return;
        GraphicsDevice device;
        try { device = ShapeTestHarness.CreateDevice(backend); }
        catch (Exception) { return; }
        using (device) Assert.True(device.Features.TimestampQueries, $"{backend} should support timestamp queries on a desktop adapter.");
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_TwoTimestampsAroundWorkload_T1GreaterThanT0(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        cl.WriteTimestamp(pool, 0);
        workload.Record(cl, repeats: 4);
        cl.WriteTimestamp(pool, 1);
        cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();

        Span<ulong> ticks = stackalloc ulong[2];
        var result = ResolveWithRetry(device, pool, 0, 2, ticks, out double ticksPerSecond);
        Assert.Equal(TimestampResolveResult.Ready, result);
        Assert.True(ticksPerSecond > 0, "ticksPerSecond must be positive");
        Assert.True(ticks[1] > ticks[0], $"t1 ({ticks[1]}) must be after t0 ({ticks[0]})");
        double ms = (ticks[1] - ticks[0]) / ticksPerSecond * 1000.0;
        Assert.InRange(ms, 0.001, 5000.0);   // sanity: 256 MB of copies is microseconds-to-milliseconds, not zero and not seconds
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_DoublingRepeatsRoughlyDoublesDelta(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        // Fill-rate-bound draws on the GRAPHICS pipeline, not CopyBuffer: on D3D11/NVIDIA copies run on a copy engine that graphics timestamps
        // do not bracket proportionally (4x copies measured 13-15x). Draws are what the timestamps are designed to measure.
        using var scene = new TimestampProbe_DrawScene(device, targetSize: 2048);
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();

        // Warm-up: first touch of fresh buffers / clocks is not representative.
        MeasureFillMs(device, scene, pool, cl, triangles: 8);

        // 4x contrast (8 vs 32 full-target triangles at 2048^2) and the MEDIAN of 5 per side: medians ignore a clock-boost or scheduler
        // outlier in either direction, where best-of-N only guards one direction. The window asserts "roughly proportional" - more work must
        // cost clearly more time, and not absurdly more - without pretending a GPU is a perfect clock.
        double small = MedianFillMs(device, scene, pool, cl, triangles: 8);
        double large = MedianFillMs(device, scene, pool, cl, triangles: 32);

        double ratio = large / small;
        Assert.InRange(ratio, 2.0, 8.0);
    }

    private static double MedianFillMs(GraphicsDevice device, TimestampProbe_DrawScene scene, TimestampQueryPool pool, CommandList cl, int triangles)
    {
        var samples = new double[5];
        for (int i = 0; i < samples.Length; i++) samples[i] = MeasureFillMs(device, scene, pool, cl, triangles);
        Array.Sort(samples);
        return samples[2];
    }

    private static double MeasureFillMs(GraphicsDevice device, TimestampProbe_DrawScene scene, TimestampQueryPool pool, CommandList cl, int triangles)
    {
        cl.Begin();
        scene.BeginPassAndDraw(cl);            // opens the pass (1 triangle, part of the warm-up of each sample)
        cl.WriteTimestamp(pool, 0);
        scene.DrawFullTargetTriangles(cl, triangles);
        cl.WriteTimestamp(pool, 1);
        cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Span<ulong> ticks = stackalloc ulong[2];
        Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 2, ticks, out double ticksPerSecond));
        return (ticks[1] - ticks[0]) / ticksPerSecond * 1000.0;
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_NotReadyBeforeSubmit_ReadyAfterWait(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        cl.WriteTimestamp(pool, 0);
        workload.Record(cl, repeats: 2);
        cl.WriteTimestamp(pool, 1);
        cl.End();

        Span<ulong> ticks = stackalloc ulong[2];
        // Recorded, ended, NOT submitted: nothing can be available. Must be NotReady and must not block or throw.
        Assert.Equal(TimestampResolveResult.NotReady, device.TryResolveTimestamps(pool, 0, 2, ticks, out _));

        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 2, ticks, out _));
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_ResolveRightAfterSubmit_ReturnsPromptlyNeverThrows(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var workload = new TimestampProbe_Workload(device);
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin();
        cl.WriteTimestamp(pool, 0);
        workload.Record(cl, repeats: 16);
        cl.WriteTimestamp(pool, 1);
        cl.End();
        device.SubmitCommands(cl);

        // C3 restatement: a fast GPU may legitimately be Ready already; the contract is "prompt and exception-free", not "NotReady".
        Span<ulong> ticks = stackalloc ulong[2];
        var sw = Stopwatch.StartNew();
        var result = device.TryResolveTimestamps(pool, 0, 2, ticks, out _);
        sw.Stop();
        Assert.True(Enum.IsDefined(result), $"unexpected result {result}");
        Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"TryResolveTimestamps took {sw.Elapsed.TotalMilliseconds:F2} ms - it must not block on the GPU");

        device.WaitForGpuIdle();
        Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 2, ticks, out _));
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_OverwriteUnresolved_IncrementsAbandonedWriteCount(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(1);
        using var cl = device.ResourceFactory.CreateCommandList();

        cl.Begin(); cl.WriteTimestamp(pool, 0); cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Assert.Equal(0UL, pool.AbandonedWriteCount);

        // Never resolved -> the second write abandons the first value. Outside a render pass, so it is legal on both backends.
        cl.Begin(); cl.WriteTimestamp(pool, 0); cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Assert.Equal(1UL, pool.AbandonedWriteCount);

        Span<ulong> ticks = stackalloc ulong[1];
        Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 1, ticks, out _));
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_ResolvedSlot_IsResetAtNextBegin_AndReusable(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();
        Span<ulong> ticks = stackalloc ulong[2];

        for (int frame = 0; frame < 5; frame++)
        {
            cl.Begin(); cl.WriteTimestamp(pool, 0); cl.WriteTimestamp(pool, 1); cl.End();
            device.SubmitCommands(cl);
            device.WaitForGpuIdle();
            Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 2, ticks, out _));
            Assert.True(ticks[1] >= ticks[0]);
        }

        Assert.Equal(0UL, pool.AbandonedWriteCount);   // every value was resolved before reuse: the ring was deep enough
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_ReuseInsideRenderPass_Throws(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(1);
        using var cl = device.ResourceFactory.CreateCommandList();
        using var scene = new TimestampProbe_DrawScene(device);

        // First use: slot 0 goes Fresh -> Written and is never resolved.
        cl.Begin(); cl.WriteTimestamp(pool, 0); cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();

        // Second recording: a DRAW opens the lazy render pass on Vulkan (clears are deferred into the pass load-op and do not) and the emulated
        // pass on D3D11; rewriting the unresolved slot inside it would need a reset that is illegal there -> VeldridException on BOTH backends
        // (parity rule 5.3 #3).
        cl.Begin();
        scene.BeginPassAndDraw(cl);
        var thrown = Record.Exception(() => cl.WriteTimestamp(pool, 0));
        Assert.True(thrown is VeldridException,
            $"expected VeldridException, got {(thrown == null ? "nothing" : thrown.GetType().Name + ": " + thrown.Message)}; abandoned={pool.AbandonedWriteCount}");
        cl.End();

        // The refused write left the slot untouched: resolving it still yields the FIRST value.
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Span<ulong> ticks = stackalloc ulong[1];
        Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 1, ticks, out _));
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_FreshSlotInsideRenderPass_IsLegal(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();
        using var scene = new TimestampProbe_DrawScene(device);

        // Writing a FRESH slot inside a pass is the normal per-pass pattern (debug-group timing relies on it). Three frames: from the second on,
        // the slots were resolved, so Begin() records their reset and they are Fresh again before the draw opens the pass.
        Span<ulong> ticks = stackalloc ulong[2];
        for (int frame = 0; frame < 3; frame++)
        {
            cl.Begin();
            scene.BeginPassAndDraw(cl);
            cl.WriteTimestamp(pool, 0);
            scene.Draw(cl);
            cl.WriteTimestamp(pool, 1);
            cl.End();
            device.SubmitCommands(cl);
            device.WaitForGpuIdle();
            Assert.Equal(TimestampResolveResult.Ready, ResolveWithRetry(device, pool, 0, 2, ticks, out _));
            Assert.True(ticks[1] >= ticks[0]);
        }

        Assert.Equal(0UL, pool.AbandonedWriteCount);
    }

    [Theory, MemberData(nameof(Backends))]
    public void ExplicitPool_Validation_RejectsBadArguments(GraphicsBackend backend)
    {
        using var device = TryCreateTimestampDevice(backend);
        if (device == null) return;
        Assert.Throws<VeldridException>(() => device.ResourceFactory.CreateTimestampQueryPool(0));
        using var pool = device.ResourceFactory.CreateTimestampQueryPool(2);
        using var cl = device.ResourceFactory.CreateCommandList();
        cl.Begin();
        Assert.Throws<VeldridException>(() => cl.WriteTimestamp(pool, 2));
        cl.End();
        ulong[] ticks = new ulong[2];
        Assert.Throws<VeldridException>(() => device.TryResolveTimestamps(pool, 1, 2, ticks, out _));
        Assert.Throws<VeldridException>(() => device.TryResolveTimestamps(pool, 0, 2, ticks.AsSpan(0, 1), out _));
    }

    // ---- helpers ----

    /// <summary>
    ///     Regression detector for WaitForGpuIdle (spec _TASKS/GpuOrderingBarrier_vs_WaitForGpuIdle.md D3): every caller has just called
    ///     device.WaitForGpuIdle(), so the FIRST resolve must already be Ready on every backend. Before Nov 10 D3D11's wait was a no-op and this
    ///     helper had to poll for up to 2 s; the poll remains only to produce a useful message if a backend regresses.
    /// </summary>
    private static TimestampResolveResult ResolveWithRetry(GraphicsDevice device, TimestampQueryPool pool, uint first, uint count, Span<ulong> ticks, out double ticksPerSecond)
    {
        var firstAttempt = device.TryResolveTimestamps(pool, first, count, ticks, out ticksPerSecond);
        if (firstAttempt != TimestampResolveResult.NotReady) return firstAttempt;

        var deadline = Stopwatch.StartNew();
        TimestampResolveResult result;
        while ((result = device.TryResolveTimestamps(pool, first, count, ticks, out ticksPerSecond)) == TimestampResolveResult.NotReady
               && deadline.Elapsed < TimeSpan.FromSeconds(2)) System.Threading.Thread.Sleep(1);
        Assert.Fail($"{device.BackendType}: TryResolveTimestamps was NotReady right after WaitForGpuIdle() (became {result} after {deadline.ElapsedMilliseconds} ms of polling). WaitForGpuIdle must block until the GPU has executed everything submitted.");
        return result;
    }

    private static double MeasureMs(GraphicsDevice device, TimestampProbe_Workload workload, TimestampQueryPool pool, CommandList cl, int repeats)
    {
        cl.Begin();
        cl.WriteTimestamp(pool, 0);
        workload.Record(cl, repeats);
        cl.WriteTimestamp(pool, 1);
        cl.End();
        device.SubmitCommands(cl);
        device.WaitForGpuIdle();
        Span<ulong> ticks = stackalloc ulong[2];
        var result = ResolveWithRetry(device, pool, 0, 2, ticks, out double ticksPerSecond);
        Assert.Equal(TimestampResolveResult.Ready, result);
        return (ticks[1] - ticks[0]) / ticksPerSecond * 1000.0;
    }
}

/// <summary>
///     GPU workload with no shader: <c>repeats</c> x CopyBuffer of a 64 MB device buffer pair. Linear in <c>repeats</c>, identical API on both
///     backends. A compute probe was considered and dropped (spec 6.1): .vdshader bundles are built out-of-tree against the published SPIRV package.
/// </summary>
internal sealed class TimestampProbe_Workload : IDisposable
{
    public const uint SizeBytes = 64 * 1024 * 1024;
    private const int destination_count = 4;

    private readonly DeviceBuffer source;
    private readonly DeviceBuffer[] destinations = new DeviceBuffer[destination_count];

    public TimestampProbe_Workload(GraphicsDevice device)
    {
        // Plain storage-less buffers: VertexBuffer usage is universally supported and never aliases a UAV/SRV path.
        source = device.ResourceFactory.CreateBuffer(new BufferDescription(SizeBytes, BufferUsage.VertexBuffer));
        source.Name = "TimestampProbe.Source";
        for (int i = 0; i < destination_count; i++)
        {
            destinations[i] = device.ResourceFactory.CreateBuffer(new BufferDescription(SizeBytes, BufferUsage.VertexBuffer));
            destinations[i].Name = $"TimestampProbe.Destination{i}";
        }
    }

    /// <summary>
    ///     Rotates the destination so consecutive copies never target the same buffer: identical back-to-back full-buffer copies into ONE
    ///     destination were measured super-linear on D3D11/NVIDIA (8 copies cost far less than 8x one copy - coalesced or overlapped), which made a
    ///     "2x work = 2x time" assertion fail with ratios of 4-6. Distinct destinations keep the cost close to proportional.
    /// </summary>
    public void Record(CommandList cl, int repeats)
    {
        for (int i = 0; i < repeats; i++) cl.CopyBuffer(source, 0, destinations[i % destination_count], 0, SizeBytes);
    }

    public void Dispose()
    {
        foreach (var d in destinations) d.Dispose();
        source.Dispose();
    }
}

/// <summary>
///     Minimal real draw (one triangle through the committed ImGui bundle) so a test can put a command list INSIDE a render pass on both
///     backends - Vulkan opens its pass lazily at the first draw, not at SetFramebuffer or a clear.
/// </summary>
internal sealed class TimestampProbe_DrawScene : IDisposable
{
    private readonly PrecompiledShaderResult bundle;
    private readonly ImGuiShape shape;
    private readonly Texture color;
    private readonly Texture white;
    private readonly Framebuffer framebuffer;
    private readonly ShaderVertexBuffer<ImGuiVertex> vertices;
    private readonly ShapeTestHarness.ImGuiFamilyResources gpu;

    /// <param name="targetSize">Render target edge in pixels. 16 for "just open a pass"; 2048 (16 MB RGBA8) for fill-rate-bound timing work.</param>
    public TimestampProbe_DrawScene(GraphicsDevice device, uint targetSize = 16)
    {
        var factory = device.ResourceFactory;
        bundle = ShapeTestHarness.LoadBundle(factory);
        shape = ShaderPipelineShape.Bind<ImGuiShape>(bundle, factory);
        shape.Projection.CreateBuffer(BufferUsage.UniformBuffer);
        color = factory.CreateTexture(TextureDescription.Texture2D(targetSize, targetSize, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
        white = ShapeTestHarness.CreateWhiteTexture(device);
        framebuffer = factory.CreateFramebuffer(new FramebufferDescription(null, color));
        vertices = shape.Vertices.CreateBuffer(3, BufferUsage.VertexBuffer | BufferUsage.Dynamic);
        gpu = ShapeTestHarness.ImGuiFamilyResources.Create(factory, bundle.Shaders, shape.Vertices, shape.Projection, shape.Projection.With(),
            shape.FontSampler, device.PointSampler, shape.FontTexture, white, framebuffer.OutputDescription, scissorTest: false);
    }

    /// <summary>SetFramebuffer + clear + uploads + one draw: after this the command list is inside a render pass on every backend.</summary>
    public void BeginPassAndDraw(CommandList cl)
    {
        cl.SetFramebuffer(framebuffer);
        cl.SetFullViewports();
        cl.SetFullScissorRects();
        cl.ClearColorTarget(0, RgbaFloat.Black);
        cl.UpdateBuffer(shape.Projection, new ImGuiProjection { projection = System.Numerics.Matrix4x4.Identity, UseLegacyColorSpaceHandling = 1 });
        cl.UpdateBuffer(vertices, 0, new ImGuiVertex[]
        {
            new() { vsin_position = new System.Numerics.Vector2(-1, -1), vsin_color = 0xff0000ff },
            new() { vsin_position = new System.Numerics.Vector2(3, -1), vsin_color = 0xff0000ff },
            new() { vsin_position = new System.Numerics.Vector2(-1, 3), vsin_color = 0xff0000ff },
        });
        Draw(cl);
    }

    /// <summary>Another draw with the state already bound (pass stays open).</summary>
    public void Draw(CommandList cl)
    {
        gpu.Bind(cl);
        cl.SetVertexBuffer(shape.Vertices, vertices);
        cl.Draw(3);
    }

    /// <summary>
    ///     Fill-rate-bound GRAPHICS-pipeline work for timing ratios: <paramref name="count"/> full-target triangles (the vertex buffer holds one
    ///     oversized triangle covering the whole target). Chosen over CopyBuffer because on D3D11/NVIDIA buffer copies run on a copy engine that
    ///     timestamps on the graphics pipeline do not bracket proportionally (4x copies measured 13-15x; see spec 6.1).
    /// </summary>
    public void DrawFullTargetTriangles(CommandList cl, int count)
    {
        for (int i = 0; i < count; i++) Draw(cl);
    }

    public void Dispose()
    {
        gpu.Dispose();
        vertices.Dispose();
        framebuffer.Dispose();
        white.Dispose();
        color.Dispose();
        shape.Dispose();
        foreach (var shader in bundle.Shaders) shader.Dispose();
    }
}