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
            graphicsDevice.WaitForIdle(); // triggers submitted-fence processing
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
        graphicsDevice.WaitForIdle();

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

        graphicsDevice.WaitForIdle();
        bool allEventuallyDestroyed = true;
        foreach (var texture in disposedThisRun)
        {
            if (!WaitForPhysicalDisposal(graphicsDevice, texture)) { allEventuallyDestroyed = false; break; }
        }
        Assert(testName + ": all disposed textures eventually destroyed (no retirement leak)",
            allEventuallyDestroyed);

        commandList.Dispose();
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