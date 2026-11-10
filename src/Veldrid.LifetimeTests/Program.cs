using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Veldrid;

namespace Veldrid.LifetimeTests;

/// <summary>
/// Integration tests for the dispose-after-record resource-lifetime contract
/// (_TASKS/BUGFIX_vulkan-draw-resourceset-refcounting.md).
///
/// Invariant under test: membership in a recording's StagingResourceInfo.Resources
/// holds exactly ONE ref, taken at RECORD time (addStagingResourceRef) and released
/// exactly once by recycleStagingInfo — after GPU completion for submitted recordings,
/// or at Begin()/CommandList.Dispose() for abandoned ones.
///
/// The tests use CopyTexture commands because they flow through the same
/// addStagingResourceRef path as draw-bound ResourceSets, but need no shaders or
/// pipelines. Physical destruction is observed via Texture.IsDisposed, which on
/// Vulkan reflects actual vkDestroyImage (VkTexture.destroyed).
///
/// Vulkan runs the full deferred-destruction assertions (the backend the fix targets).
/// D3D11/Metal run the same sequences as no-crash smoke coverage: their IsDisposed
/// flips immediately on Dispose() because the OS runtime (COM refcounts / ObjC retain)
/// provides the deferral below the API — the strict timing assertions are Vulkan-only.
/// </summary>
internal static class Program
{
    private static int passCount;
    private static int failCount;
    private static readonly List<string> failures = new();

    static int Main()
    {
        Console.WriteLine("═════════════════════════════════════════════════════");
        Console.WriteLine("  Veldrid Resource Lifetime Tests (dispose-after-record)");
        Console.WriteLine("═════════════════════════════════════════════════════");
        Console.WriteLine();

        bool anyBackendRan = false;

        if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan))
        {
            anyBackendRan |= RunBackend(GraphicsBackend.Vulkan, strictDeferredAssertions: true);
        }
        else
        {
            Console.WriteLine("SKIP: Vulkan backend not supported on this machine.");
        }

        if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Direct3D11))
        {
            anyBackendRan |= RunBackend(GraphicsBackend.Direct3D11, strictDeferredAssertions: false);
        }

        if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Metal))
        {
            anyBackendRan |= RunBackend(GraphicsBackend.Metal, strictDeferredAssertions: false);
        }

        Console.WriteLine();
        Console.WriteLine("═════════════════════════════════════════════════════");
        Console.WriteLine($"  Results: {passCount} passed, {failCount} failed");
        Console.WriteLine("═════════════════════════════════════════════════════");
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FAILURES:");
            foreach (var failureText in failures) Console.WriteLine($"  ✗ {failureText}");
        }
        if (!anyBackendRan)
        {
            Console.WriteLine("SKIP: no supported backend available — nothing tested.");
        }
        return failCount > 0 ? 1 : 0;
    }

    private static bool RunBackend(GraphicsBackend backend, bool strictDeferredAssertions)
    {
        GraphicsDevice graphicsDevice;
        try
        {
            var options = new GraphicsDeviceOptions(debug: false);
            graphicsDevice = backend switch
            {
                GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options),
                GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options),
                GraphicsBackend.Metal => GraphicsDevice.CreateMetal(options),
                _ => throw new NotSupportedException(backend.ToString()),
            };
        }
        catch (Exception deviceCreateException)
        {
            Console.WriteLine($"SKIP {backend}: device creation failed: {deviceCreateException.Message}");
            return false;
        }

        Console.WriteLine($"── {backend} ({graphicsDevice.DeviceName}) — strict deferred assertions: {strictDeferredAssertions} ──");
        try
        {
            Test_DisposeAfterRecord_BeforeSubmit_DefersDestruction(graphicsDevice, backend, strictDeferredAssertions);
            Test_AbandonedRecording_DoesNotDestroyOwnedResources(graphicsDevice, backend, strictDeferredAssertions);
            Test_CommandListDispose_WithUnsubmittedRecording_ReleasesRefs(graphicsDevice, backend, strictDeferredAssertions);
            Test_RepeatedRecordDisposeSubmitCycles_NoLeakNoCrash(graphicsDevice, backend);
            Test_DeviceUpload_SingleThread_RoundTrip(graphicsDevice, backend);
            Test_DeviceUpload_OffThread_WhileSubmitting_NoRaceNoLeak(graphicsDevice, backend);
        }
        finally
        {
            graphicsDevice.Dispose();
        }
        Console.WriteLine();
        return true;
    }

    private static Texture CreateSampledTexture(GraphicsDevice graphicsDevice)
    {
        return graphicsDevice.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
            16, 16, mipLevels: 1, arrayLayers: 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
    }

    /// <summary>Waits for the device to notice GPU completion and run recycleStagingInfo
    /// (which releases the record-time refs). Polls IsDisposed up to 2 seconds.</summary>
    private static bool WaitForPhysicalDisposal(GraphicsDevice graphicsDevice, Texture texture)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            graphicsDevice.WaitForGpuIdle(); // triggers submitted-fence processing
            if (texture.IsDisposed) return true;
            Thread.Sleep(10);
        }
        return texture.IsDisposed;
    }

    // ───────────────────────────────────────────────────────────────
    // GAP 1 regression: the record→submit window.
    // record copy → End → Dispose(texture) → Submit → complete.
    // Pre-fix Vulkan: Dispose destroyed the VkImage immediately (refs were only
    // incremented at submission), so the later submit executed against destroyed
    // handles. Post-fix: destruction is deferred until GPU completion.
    // ───────────────────────────────────────────────────────────────
    private static void Test_DisposeAfterRecord_BeforeSubmit_DefersDestruction(
        GraphicsDevice graphicsDevice, GraphicsBackend backend, bool strictDeferredAssertions)
    {
        string testName = $"{backend}: DisposeAfterRecord_BeforeSubmit";
        Texture sourceTexture = CreateSampledTexture(graphicsDevice);
        Texture destinationTexture = CreateSampledTexture(graphicsDevice);
        CommandList commandList = graphicsDevice.ResourceFactory.CreateCommandList();

        commandList.Begin();
        commandList.CopyTexture(sourceTexture, destinationTexture);
        commandList.End();

        // THE window: dispose after recording, BEFORE submission.
        sourceTexture.Dispose();

        if (strictDeferredAssertions)
        {
            Assert(testName + ": destruction deferred while recording holds ref",
                !sourceTexture.IsDisposed);
        }

        graphicsDevice.SubmitCommands(commandList); // pre-fix Vulkan: destroyed handles here
        graphicsDevice.WaitForGpuIdle();

        if (strictDeferredAssertions)
        {
            Assert(testName + ": destruction occurs after GPU completion",
                WaitForPhysicalDisposal(graphicsDevice, sourceTexture));
        }
        else
        {
            Assert(testName + ": no crash through submit/complete", true);
        }

        commandList.Dispose();
        destinationTexture.Dispose();
    }

    // ───────────────────────────────────────────────────────────────
    // GAP 2 regression: abandoned recording (End without submit, then Begin again).
    // Pre-fix: Begin()'s recycleStagingInfo DECREMENTED refs that were never
    // incremented (submission never happened), destroying textures the caller
    // still owns. Post-fix: recycle releases exactly the record-time ref; the
    // owner's ref survives.
    // ───────────────────────────────────────────────────────────────
    private static void Test_AbandonedRecording_DoesNotDestroyOwnedResources(
        GraphicsDevice graphicsDevice, GraphicsBackend backend, bool strictDeferredAssertions)
    {
        string testName = $"{backend}: AbandonedRecording";
        Texture sourceTexture = CreateSampledTexture(graphicsDevice);
        Texture destinationTexture = CreateSampledTexture(graphicsDevice);
        CommandList commandList = graphicsDevice.ResourceFactory.CreateCommandList();

        commandList.Begin();
        commandList.CopyTexture(sourceTexture, destinationTexture);
        commandList.End();
        // NOT submitted — abandon by beginning a fresh recording.
        commandList.Begin();

        if (strictDeferredAssertions)
        {
            Assert(testName + ": owned textures survive abandoned-recording recycle",
                !sourceTexture.IsDisposed && !destinationTexture.IsDisposed);
        }

        commandList.End();
        sourceTexture.Dispose();
        destinationTexture.Dispose();

        if (strictDeferredAssertions)
        {
            Assert(testName + ": owner Dispose() destroys (no leaked record-time ref)",
                sourceTexture.IsDisposed && destinationTexture.IsDisposed);
        }
        else
        {
            Assert(testName + ": no crash through abandon/redispose", true);
        }

        commandList.Dispose();
    }

    // ───────────────────────────────────────────────────────────────
    // CommandList.Dispose() with a recorded-but-unsubmitted recording must release
    // the record-time refs (disposeCore now recycles currentStagingInfo) — no leak:
    // the owner's later Dispose() must be the one that physically destroys.
    // ───────────────────────────────────────────────────────────────
    private static void Test_CommandListDispose_WithUnsubmittedRecording_ReleasesRefs(
        GraphicsDevice graphicsDevice, GraphicsBackend backend, bool strictDeferredAssertions)
    {
        string testName = $"{backend}: CommandListDispose_Unsubmitted";
        Texture sourceTexture = CreateSampledTexture(graphicsDevice);
        Texture destinationTexture = CreateSampledTexture(graphicsDevice);
        CommandList commandList = graphicsDevice.ResourceFactory.CreateCommandList();

        commandList.Begin();
        commandList.CopyTexture(sourceTexture, destinationTexture);
        commandList.End();
        commandList.Dispose(); // never submitted

        if (strictDeferredAssertions)
        {
            Assert(testName + ": owned textures survive CommandList dispose",
                !sourceTexture.IsDisposed && !destinationTexture.IsDisposed);
        }

        sourceTexture.Dispose();
        destinationTexture.Dispose();

        if (strictDeferredAssertions)
        {
            Assert(testName + ": owner Dispose() destroys (record-time refs were released)",
                sourceTexture.IsDisposed && destinationTexture.IsDisposed);
        }
        else
        {
            Assert(testName + ": no crash through CL-dispose/redispose", true);
        }
    }

    // ───────────────────────────────────────────────────────────────
    // Churn: the SilkyNvg per-frame pattern (create → record → dispose → submit)
    // repeated many times. Pins no-crash and no unbounded retirement leak.
    // ───────────────────────────────────────────────────────────────
    private static void Test_RepeatedRecordDisposeSubmitCycles_NoLeakNoCrash(
        GraphicsDevice graphicsDevice, GraphicsBackend backend)
    {
        string testName = $"{backend}: RepeatedRecordDisposeSubmitCycles(x50)";
        CommandList commandList = graphicsDevice.ResourceFactory.CreateCommandList();
        var disposedThisRun = new List<Texture>();

        for (int cycleIndex = 0; cycleIndex < 50; cycleIndex++)
        {
            Texture sourceTexture = CreateSampledTexture(graphicsDevice);
            Texture destinationTexture = CreateSampledTexture(graphicsDevice);
            commandList.Begin();
            commandList.CopyTexture(sourceTexture, destinationTexture);
            commandList.End();
            sourceTexture.Dispose();       // before submit — the Gap 1 window, every cycle
            graphicsDevice.SubmitCommands(commandList);
            destinationTexture.Dispose();  // after submit, before completion
            disposedThisRun.Add(sourceTexture);
            disposedThisRun.Add(destinationTexture);
        }

        graphicsDevice.WaitForGpuIdle();
        bool allEventuallyDestroyed = true;
        foreach (var texture in disposedThisRun)
        {
            if (!WaitForPhysicalDisposal(graphicsDevice, texture)) { allEventuallyDestroyed = false; break; }
        }
        Assert(testName + ": all disposed textures eventually destroyed (no retirement leak)",
            allEventuallyDestroyed);

        commandList.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Device-side upload path (GraphicsDevice.UpdateTexture / UpdateBuffer, no CommandList).
    // On Vulkan these go through SharedCommandPool + a pooled staging resource, recycled
    // when the submission fence signals (_BUGFIX/BUGFIX_Vulkan_SharedCommandPool_RegisterAfterSubmit_Race.md).
    // ─────────────────────────────────────────────────────────────────────────────────

    private const uint upload_tile_edge = 16;                 // 16x16x16 R8G8B8A8 = 16 KiB per tile
    private const uint upload_small_buffer_bytes = 256;       // <= Vulkan max_staging_buffer_size (pooled staging path)
    private const uint upload_large_buffer_bytes = 4096;      // >  Vulkan max_staging_buffer_size (dispose-after-use path)

    private static Texture CreateUploadTargetTexture3D(GraphicsDevice graphicsDevice)
    {
        return graphicsDevice.ResourceFactory.CreateTexture(TextureDescription.Texture3D(
            upload_tile_edge, upload_tile_edge, upload_tile_edge, mipLevels: 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
    }

    private static Texture CreateReadbackStagingTexture3D(GraphicsDevice graphicsDevice)
    {
        return graphicsDevice.ResourceFactory.CreateTexture(TextureDescription.Texture3D(
            upload_tile_edge, upload_tile_edge, upload_tile_edge, mipLevels: 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));
    }

    /// <summary>Fills every texel/word of the tile with the same 32-bit <paramref name="fillWord"/>.</summary>
    private static uint[] MakeFilledWords(uint byteCount, uint fillWord)
    {
        var words = new uint[byteCount / sizeof(uint)];
        Array.Fill(words, fillWord);
        return words;
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="stagingReadback"/> on a fresh command list, waits, then maps and counts words != expected.</summary>
    private static int CountBadTexels(GraphicsDevice graphicsDevice, Texture source, Texture stagingReadback, uint expectedWord)
    {
        using CommandList readbackList = graphicsDevice.ResourceFactory.CreateCommandList();
        readbackList.Begin();
        readbackList.CopyTexture(source, stagingReadback);
        readbackList.End();
        graphicsDevice.SubmitCommands(readbackList);
        graphicsDevice.WaitForGpuIdle();

        int badTexels = 0;
        MappedResource mapped = graphicsDevice.Map(stagingReadback, MapMode.Read);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)mapped.Data;
                for (uint z = 0; z < upload_tile_edge; z++)
                for (uint y = 0; y < upload_tile_edge; y++)
                {
                    uint* row = (uint*)(basePtr + z * mapped.DepthPitch + y * mapped.RowPitch);
                    for (uint x = 0; x < upload_tile_edge; x++) if (row[x] != expectedWord) badTexels++;
                }
            }
        }
        finally { graphicsDevice.Unmap(stagingReadback); }
        return badTexels;
    }

    private static int CountBadWords(GraphicsDevice graphicsDevice, DeviceBuffer source, DeviceBuffer stagingReadback, uint byteCount, uint expectedWord)
    {
        using CommandList readbackList = graphicsDevice.ResourceFactory.CreateCommandList();
        readbackList.Begin();
        readbackList.CopyBuffer(source, 0, stagingReadback, 0, byteCount);
        readbackList.End();
        graphicsDevice.SubmitCommands(readbackList);
        graphicsDevice.WaitForGpuIdle();

        int badWords = 0;
        MappedResourceView<uint> view = graphicsDevice.Map<uint>(stagingReadback, MapMode.Read);
        try
        {
            for (int wordIndex = 0; wordIndex < view.Count; wordIndex++) if (view[wordIndex] != expectedWord) badWords++;
        }
        finally { graphicsDevice.Unmap(stagingReadback); }
        return badWords;
    }

    /// <summary>Vulkan-only leak probe: in-flight SharedCommandPool submissions must be 0 after WaitForIdle. Returns null on other backends.</summary>
    private static int? VulkanInFlightSharedPoolSubmissions(GraphicsDevice graphicsDevice)
    {
        return graphicsDevice is Veldrid.Vk.VkGraphicsDevice vkDevice ? vkDevice.InFlightSharedPoolSubmissionCount_ForTests : null;
    }

    // Basic functionality: single-threaded UpdateTexture (3D tile) + UpdateBuffer (pooled and oversize staging) round-trip.
    private static void Test_DeviceUpload_SingleThread_RoundTrip(GraphicsDevice graphicsDevice, GraphicsBackend backend)
    {
        string testName = $"{backend}: DeviceUpload_SingleThread_RoundTrip";
        Texture targetTexture = CreateUploadTargetTexture3D(graphicsDevice);
        Texture readbackTexture = CreateReadbackStagingTexture3D(graphicsDevice);
        DeviceBuffer smallBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_small_buffer_bytes, BufferUsage.VertexBuffer) { AllowUpdateBufferOutsideCommandList = true });
        DeviceBuffer largeBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_large_buffer_bytes, BufferUsage.VertexBuffer) { AllowUpdateBufferOutsideCommandList = true });
        DeviceBuffer smallReadback = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_small_buffer_bytes, BufferUsage.Staging));
        DeviceBuffer largeReadback = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_large_buffer_bytes, BufferUsage.Staging));

        uint tileBytes = upload_tile_edge * upload_tile_edge * upload_tile_edge * 4;
        for (uint pass = 1; pass <= 3; pass++)
        {
            uint fillWord = 0xA0000000u | pass;
            graphicsDevice.UpdateTexture(targetTexture, MakeFilledWords(tileBytes, fillWord), 0, 0, 0, upload_tile_edge, upload_tile_edge, upload_tile_edge, 0, 0);
            graphicsDevice.UpdateBufferOutsideCommandList(smallBuffer, 0, MakeFilledWords(upload_small_buffer_bytes, fillWord));
            graphicsDevice.UpdateBufferOutsideCommandList(largeBuffer, 0, MakeFilledWords(upload_large_buffer_bytes, fillWord));

            Assert($"{testName}: pass {pass} texture texels correct",
                CountBadTexels(graphicsDevice, targetTexture, readbackTexture, fillWord) == 0);
            Assert($"{testName}: pass {pass} small buffer words correct",
                CountBadWords(graphicsDevice, smallBuffer, smallReadback, upload_small_buffer_bytes, fillWord) == 0);
            Assert($"{testName}: pass {pass} large buffer words correct",
                CountBadWords(graphicsDevice, largeBuffer, largeReadback, upload_large_buffer_bytes, fillWord) == 0);
        }

        graphicsDevice.WaitForGpuIdle();
        int? inFlight = VulkanInFlightSharedPoolSubmissions(graphicsDevice);
        if (inFlight.HasValue)
            Assert($"{testName}: no in-flight shared-pool submissions after WaitForIdle (got {inFlight})", inFlight == 0);

        targetTexture.Dispose(); readbackTexture.Dispose();
        smallBuffer.Dispose(); largeBuffer.Dispose(); smallReadback.Dispose(); largeReadback.Dispose();
    }

    // Concurrency regression: thread B hammers GraphicsDevice.UpdateTexture/UpdateBuffer while thread A records+submits
    // command lists copying the same resources (every SubmitCommands runs checkSubmittedFences). Pre-fix Vulkan:
    // "An item with the same key has already been added. Key: Vulkan.VkCommandBuffer" then an access violation,
    // with a shared-pool leak visible before the crash. Post-fix: no exception, final values intact, 0 in flight after idle.
    private static void Test_DeviceUpload_OffThread_WhileSubmitting_NoRaceNoLeak(GraphicsDevice graphicsDevice, GraphicsBackend backend)
    {
        const int upload_iterations = 2000;
        string testName = $"{backend}: DeviceUpload_OffThread_WhileSubmitting(x{upload_iterations})";
        Texture targetTexture = CreateUploadTargetTexture3D(graphicsDevice);
        Texture readbackTexture = CreateReadbackStagingTexture3D(graphicsDevice);
        Texture copyScratchTexture = CreateUploadTargetTexture3D(graphicsDevice);
        DeviceBuffer smallBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_small_buffer_bytes, BufferUsage.VertexBuffer) { AllowUpdateBufferOutsideCommandList = true });
        DeviceBuffer largeBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_large_buffer_bytes, BufferUsage.VertexBuffer) { AllowUpdateBufferOutsideCommandList = true });
        DeviceBuffer copyScratchBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_large_buffer_bytes, BufferUsage.VertexBuffer) { AllowUpdateBufferOutsideCommandList = true });
        DeviceBuffer smallReadback = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_small_buffer_bytes, BufferUsage.Staging));
        DeviceBuffer largeReadback = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(upload_large_buffer_bytes, BufferUsage.Staging));

        uint tileBytes = upload_tile_edge * upload_tile_edge * upload_tile_edge * 4;
        uint finalFillWord = 0xB0000000u | (upload_iterations - 1);
        Exception uploadThreadException = null;
        Exception submitThreadException = null;
        int submitCount = 0;
        var uploadsDone = new ManualResetEventSlim(false);

        var uploadThread = new Thread(() =>
        {
            try
            {
                for (uint iteration = 0; iteration < upload_iterations; iteration++)
                {
                    uint fillWord = 0xB0000000u | iteration;
                    graphicsDevice.UpdateTexture(targetTexture, MakeFilledWords(tileBytes, fillWord), 0, 0, 0, upload_tile_edge, upload_tile_edge, upload_tile_edge, 0, 0);
                    graphicsDevice.UpdateBufferOutsideCommandList(smallBuffer, 0, MakeFilledWords(upload_small_buffer_bytes, fillWord));
                    graphicsDevice.UpdateBufferOutsideCommandList(largeBuffer, 0, MakeFilledWords(upload_large_buffer_bytes, fillWord));
                }
            }
            catch (Exception uploadException) { uploadThreadException = uploadException; }
            finally { uploadsDone.Set(); }
        }) { Name = "LifetimeTests-UploadThread", IsBackground = true };

        var submitThread = new Thread(() =>
        {
            try
            {
                using CommandList copyList = graphicsDevice.ResourceFactory.CreateCommandList();
                while (!uploadsDone.IsSet)
                {
                    copyList.Begin();
                    copyList.CopyTexture(targetTexture, copyScratchTexture);
                    copyList.CopyBuffer(largeBuffer, 0, copyScratchBuffer, 0, upload_large_buffer_bytes);
                    copyList.End();
                    graphicsDevice.SubmitCommands(copyList); // runs checkSubmittedFences -> the completion side of the race
                    submitCount++;
                }
            }
            catch (Exception submitException) { submitThreadException = submitException; }
        }) { Name = "LifetimeTests-SubmitThread", IsBackground = true };

        var stopwatch = Stopwatch.StartNew();
        uploadThread.Start();
        submitThread.Start();
        bool finished = uploadThread.Join(TimeSpan.FromSeconds(60)) & submitThread.Join(TimeSpan.FromSeconds(60));
        graphicsDevice.WaitForGpuIdle();
        Console.WriteLine($"    ({upload_iterations} upload iterations, {submitCount} concurrent submits, {stopwatch.ElapsedMilliseconds} ms)");
        if (uploadThreadException != null) Console.WriteLine($"    UPLOAD THREAD EXCEPTION:\n{uploadThreadException}");
        if (submitThreadException != null) Console.WriteLine($"    SUBMIT THREAD EXCEPTION:\n{submitThreadException}");

        Assert($"{testName}: both threads finished within timeout", finished);
        Assert($"{testName}: upload thread threw no exception" + (uploadThreadException != null ? $" [{uploadThreadException.GetType().Name}: {uploadThreadException.Message}]" : ""),
            uploadThreadException == null);
        Assert($"{testName}: submit thread threw no exception" + (submitThreadException != null ? $" [{submitThreadException.GetType().Name}: {submitThreadException.Message}]" : ""),
            submitThreadException == null);

        if (uploadThreadException == null && submitThreadException == null)
        {
            Assert($"{testName}: final texture texels == last upload",
                CountBadTexels(graphicsDevice, targetTexture, readbackTexture, finalFillWord) == 0);
            Assert($"{testName}: final small buffer words == last upload",
                CountBadWords(graphicsDevice, smallBuffer, smallReadback, upload_small_buffer_bytes, finalFillWord) == 0);
            Assert($"{testName}: final large buffer words == last upload",
                CountBadWords(graphicsDevice, largeBuffer, largeReadback, upload_large_buffer_bytes, finalFillWord) == 0);
        }

        int? inFlight = VulkanInFlightSharedPoolSubmissions(graphicsDevice);
        if (inFlight.HasValue)
            Assert($"{testName}: no in-flight shared-pool submissions after WaitForIdle (got {inFlight})", inFlight == 0);

        targetTexture.Dispose(); readbackTexture.Dispose(); copyScratchTexture.Dispose();
        smallBuffer.Dispose(); largeBuffer.Dispose(); copyScratchBuffer.Dispose(); smallReadback.Dispose(); largeReadback.Dispose();
    }

    // ────────────────────────────── Assertion helper ─────────────────────────

    private static void Assert(string assertionName, bool condition)
    {
        if (condition)
        {
            Console.WriteLine($"  ✓ {assertionName}");
            passCount++;
        }
        else
        {
            Console.WriteLine($"  ✗ {assertionName}");
            failCount++;
            failures.Add(assertionName);
        }
    }
}