// Veldrid.ImGui.SDL2.Test - standalone smoke test for the ImGui <-> SDL2 InputSnapshot binding.
//
// Drives the full windowing-agnostic frame lifecycle through the SDL2 example binding:
//   window.PumpEvents() -> renderer.BeginFrame(dt) -> renderer.FeedInputSnapshot(snapshot, window) -> renderer.EndFrame()
//   -> ImGui.ShowDemoWindow() -> renderer.Render(gd, cl) -> gd.SwapBuffers()
// and also exercises the drop-in `renderer.Update(dt, snapshot)` extension once so that path compiles.
//
// Usage:  Veldrid.ImGui.SDL2.Test [--frames N]     (N > 0 auto-exits after N frames; default runs until the window closes)
//
// Manual checks while it runs: mouse hover/click, wheel, text input in the demo window, F-keys, arrows, modifiers,
// alt-tab away (focus loss should release held buttons/keys).

using ImGuiNET;
using System;
using System.Diagnostics;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace Veldrid.ImGuiSdl2Test
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int autoExitFrameCount = ParseFramesArg(args);

            WindowCreateInfo windowCreateInfo = new WindowCreateInfo(
                x: 100, y: 100, windowWidth: 1280, windowHeight: 720,
                WindowState.Normal, "Veldrid.ImGui.SDL2.Test");
            GraphicsDeviceOptions graphicsDeviceOptions = new GraphicsDeviceOptions(
                debug: false, swapchainDepthFormat: null, syncToVerticalBlank: true);

            VeldridStartup.CreateWindowAndGraphicsDevice(windowCreateInfo, graphicsDeviceOptions, out Sdl2Window window, out GraphicsDevice gd);
            Console.WriteLine($"Backend: {gd.BackendType}");

            CommandList commandList = gd.ResourceFactory.CreateCommandList();
            ImGuiRenderer imguiRenderer = new ImGuiRenderer(gd, gd.MainSwapchain.Framebuffer.OutputDescription, window.Width, window.Height);

            window.Resized += () =>
            {
                gd.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
                imguiRenderer.WindowResized(window.Width, window.Height);
            };

            Stopwatch frameStopwatch = Stopwatch.StartNew();
            int renderedFrameCount = 0;
            bool showDemoWindow = true;

            while (window.Exists)
            {
                float deltaSeconds = (float)frameStopwatch.Elapsed.TotalSeconds;
                frameStopwatch.Restart();

                InputSnapshot inputSnapshot = window.PumpEvents();
                if (!window.Exists) { break; }

                if (renderedFrameCount == 0)
                {
                    // Exercise the drop-in path once: exact replacement for the old ImGuiRenderer.Update(float, InputSnapshot).
                    imguiRenderer.Update(deltaSeconds, inputSnapshot);
                }
                else
                {
                    // The 3-call path: this is what a non-InputSnapshot host does with its own Add*Event mapping.
                    imguiRenderer.BeginFrame(deltaSeconds);
                    imguiRenderer.FeedInputSnapshot(inputSnapshot, window);
                    imguiRenderer.EndFrame();
                }

                ImGui.ShowDemoWindow(ref showDemoWindow);
                ImGui.Begin("Veldrid.ImGui.SDL2.Test");
                ImGui.Text($"Frame {renderedFrameCount}   dt {deltaSeconds * 1000f:F2} ms   focused: {window.Focused}");
                ImGui.Text($"Mouse {inputSnapshot.MousePosition.X:F0},{inputSnapshot.MousePosition.Y:F0}   wheel {inputSnapshot.WheelDelta:F1}   keys this frame: {inputSnapshot.KeyEvents.Count}   chars: {inputSnapshot.KeyCharPresses.Count}");
                ImGui.End();

                commandList.Begin();
                commandList.SetFramebuffer(gd.MainSwapchain.Framebuffer);
                commandList.ClearColorTarget(0, new RgbaFloat(0.10f, 0.12f, 0.16f, 1f));
                imguiRenderer.Render(gd, commandList);
                commandList.End();
                gd.SubmitCommands(commandList);
                gd.SwapBuffers(gd.MainSwapchain);

                renderedFrameCount++;
                if (autoExitFrameCount > 0 && renderedFrameCount >= autoExitFrameCount)
                {
                    Console.WriteLine($"Rendered {renderedFrameCount} frames; auto-exit.");
                    window.Close();
                }
            }

            gd.WaitForIdle();
            imguiRenderer.Dispose();
            commandList.Dispose();
            gd.Dispose();
            return 0;
        }

        private static int ParseFramesArg(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--frames" && int.TryParse(args[i + 1], out int frameCount))
                {
                    return frameCount;
                }
            }
            return 0;
        }
    }
}