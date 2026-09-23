# Veldrid.ImGui: Decouple from StartupUtilities / SDL2

**Status:** IMPLEMENTED 2026-09-22 (4.10 publish + Arcane pickup pending)  
**Origin:** Arcane Siege roadmap prerequisite (`C:\PROJECTS\AN_DOCS\VesselDoc_Planning\ArcaneSiege_Phased_Roadmap.html`, record `veldrid-imgui-decouple`)  
**Consumers waiting on this:** Arcane Siege (AN.Windowing + FluidUI.GameCompositor host, ImGui kept as debug-only overlay)

---

## 1. Problem

`Veldrid.ImGui` transitively drags SDL2 into every consumer:

```
Veldrid.ImGui ──► Veldrid.StartupUtilities ──► Veldrid.SDL2 ──► SDL2 native
```

`ImGuiRenderer.cs` calls **nothing** from `Veldrid.StartupUtilities` and nothing from SDL2. The reference exists only because these input-shape types were (upstream) declared in the `Veldrid.SDL2` assembly under `namespace Veldrid`:

| Type | File | Used by ImGuiRenderer as |
|---|---|---|
| `InputSnapshot` (interface) | `src/Veldrid.SDL2/InputSnapshot.cs` | parameter of `Update(float, InputSnapshot)` |
| `KeyEvent`, `Key` (enum) | `src/Veldrid.SDL2/KeyEvent.cs` | `TryMapKey(Key, out ImGuiKey)` — ~160-line switch, lines 422–588 |
| `MouseEvent`, `MouseButton` (enum) | `src/Veldrid.SDL2/MouseEvent.cs` | `snapshot.IsMouseDown(MouseButton.*)` in `UpdateImGuiInput` |
| `ModifierKeys` | `src/Veldrid.SDL2/ModifierKeys.cs` | not used |

Only two members touch these types: `Update(float deltaSeconds, InputSnapshot snapshot)` (line 377) and `UpdateImGuiInput(InputSnapshot)` (lines 590–614) + its helper `TryMapKey`. Everything else (`Render`, `WindowResized`, `RecreateFontDeviceTexture`, texture binding, pipeline creation, `BeginUpdate`/`EndUpdate`, `SetPerFrameImGuiData`) is pure Veldrid + ImGui.NET.

Hosts that are not SDL2 (AN.Windowing) currently must fabricate a fake `InputSnapshot` implementation with accumulation buffers just to feed ImGui — throwaway glue.

## 2. Decision

**No new input interface.** ImGui ≥ 1.87 already has an event-queue input model (`ImGuiIO.AddMousePosEvent / AddMouseButtonEvent / AddMouseWheelEvent / AddKeyEvent / AddInputCharacter`). Backends are expected to push events directly. We mirror that contract 1:1 on `ImGuiRenderer` and move the SDL2-shaped `InputSnapshot` path into a separate **example binding** assembly.

Rationale: an `IImGuiInputSource` interface would just re-describe `ImGuiIO`'s event API behind one more indirection and force every host to reinvent an event-buffer struct. Mirroring upstream ImGui's backend contract is 100% compliance, not a local invention.

## 3. Target shape

```
src/Veldrid.ImGui/             ArtificialNecessity.Veldrid.ImGui        deps: Veldrid, ImGui.NET     (NO StartupUtilities, NO SDL2)
src/Veldrid.ImGui.SDL2/        ArtificialNecessity.Veldrid.ImGui.SDL2   deps: Veldrid.ImGui, Veldrid.SDL2   (example binding: InputSnapshot adapter)
```

### 3.1 `Veldrid.ImGui` — `ImGuiRenderer` public surface after the change

```csharp
namespace Veldrid
{
    public partial class ImGuiRenderer : IDisposable
    {
        // ---- unchanged ----
        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height);
        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, ColorSpaceHandling colorSpaceHandling);
        public void WindowResized(int width, int height);
        public void RecreateFontDeviceTexture();
        public void RecreateFontDeviceTexture(GraphicsDevice gd);
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView);   // (existing texture-binding API, keep as-is)
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture);
        public void RemoveImGuiBinding(TextureView textureView);
        public void RemoveImGuiBinding(Texture texture);
        public void ClearCachedImageResources();
        public void Render(GraphicsDevice gd, CommandList cl);
        public void Dispose();

        // ---- NEW: windowing-agnostic frame lifecycle (promote existing protected BeginUpdate/EndUpdate) ----
        /// Ends the previous frame if still open, sets DisplaySize/DisplayFramebufferScale/DeltaTime. Call once per frame BEFORE feeding input.
        public void BeginFrame(float deltaSeconds);
        /// Calls ImGui.NewFrame(). Call once per frame AFTER feeding input. ImGui.* widget calls go between EndFrame() and Render().
        public void EndFrame();

        // ---- NEW: input feed, 1:1 with ImGuiIO.Add*Event; host owns key mapping ----
        public void AddMousePosEvent(float x, float y);                        // io.AddMousePosEvent
        public void AddMouseButtonEvent(ImGuiMouseButton button, bool down);   // io.AddMouseButtonEvent((int)button, down)
        public void AddMouseWheelEvent(float wheelX, float wheelY);            // io.AddMouseWheelEvent
        public void AddInputCharacter(uint unicodeCodepoint);                  // io.AddInputCharacter
        public void AddKeyEvent(ImGuiKey key, bool down);                      // io.AddKeyEvent (modifiers are ImGuiKey.ModCtrl/ModShift/ModAlt/ModSuper)
        public void AddFocusEvent(bool focused);                               // io.AddFocusEvent (window lost/gained focus)

        // ---- NEW: DPI / render-scale ----
        /// Set framebuffer scale independently of WindowResized (e.g. host renders ImGui into a down-scaled game texture).
        public void SetDisplayFramebufferScale(Vector2 scaleFactor);
    }
}
```

Notes:
- `BeginFrame` / `EndFrame` are the existing `protected BeginUpdate` / `protected EndUpdate` made public and renamed. **No `[Obsolete]` forwarders** — nothing under `C:\PROJECTS` subclasses `ImGuiRenderer` (decision, §7).
- `_scaleFactor` already exists as a private field used by `SetPerFrameImGuiData` — `SetDisplayFramebufferScale` just exposes it.
- `AddMouseButtonEvent` takes `ImGuiMouseButton` (ImGui.NET enum: Left=0, Right=1, Middle=2) — no Veldrid type. Buttons 3/4 are `(ImGuiMouseButton)3` / `(ImGuiMouseButton)4` per ImGui convention.
- `WindowResized` semantics unchanged: sets `_windowWidth/_windowHeight` in **framebuffer pixels**; `DisplaySize = size / scaleFactor`.
- Also public and unchanged (omitted from the listing above for brevity, present in `.stableapi`): `DestroyDeviceObjects()`, `CreateDeviceResources(...)` ×2, `GetImageResourceSet(IntPtr)`.

### 3.2 `Veldrid.ImGui.SDL2` — example binding (the moved code)

```csharp
namespace Veldrid
{
    /// SDL2 / Veldrid.SDL2 InputSnapshot binding for ImGuiRenderer. Reference implementation for other windowing hosts.
    public static class ImGuiRendererInputSnapshotExtensions
    {
        /// Drop-in replacement for the old ImGuiRenderer.Update(float, InputSnapshot).
        public static void Update(this ImGuiRenderer renderer, float deltaSeconds, InputSnapshot snapshot)
        {
            renderer.BeginFrame(deltaSeconds);
            renderer.FeedInputSnapshot(snapshot);
            renderer.EndFrame();
        }

        /// Moved body of UpdateImGuiInput: MousePosition, IsMouseDown(Left/Right/Middle/Button1/Button2), WheelDelta, KeyCharPresses, KeyEvents.
        public static void FeedInputSnapshot(this ImGuiRenderer renderer, InputSnapshot snapshot);

        /// Same as above, plus window focus: renderer.AddFocusEvent(window.Focused).
        /// Included so the reference binding demonstrates every Add*Event on the core surface.
        public static void FeedInputSnapshot(this ImGuiRenderer renderer, InputSnapshot snapshot, Sdl2Window window)
        {
            renderer.FeedInputSnapshot(snapshot);
            renderer.AddFocusEvent(window.Focused);
        }

        /// Moved TryMapKey (Veldrid.Key -> ImGuiKey), made public so other bindings can crib the table.
        public static bool TryMapKey(Key key, out ImGuiKey result);
    }
}
```

Same `namespace Veldrid`, same method name `Update` → existing call sites (`imguiRenderer.Update(deltaSeconds, inputSnapshot)`) compile unchanged once the consumer adds the `ArtificialNecessity.Veldrid.ImGui.SDL2` package reference. (`InputSnapshot` has no focus information, so `Update` cannot feed focus; hosts wanting it use `BeginFrame` + `FeedInputSnapshot(snapshot, window)` + `EndFrame` as the test project does.)

## 4. Implementation steps

- [x] **4.1** `src/Veldrid.ImGui/Veldrid.ImGui.csproj`: remove the `Veldrid.StartupUtilities` `ProjectReference` (lines 70–72). Keep `Veldrid` + `ImGui.NET 1.90.1.1`.
- [x] **4.2** `ImGuiRenderer.cs`: delete `Update(float, InputSnapshot)`, `UpdateImGuiInput(InputSnapshot)`, `TryMapKey(Key, out ImGuiKey)`. Build must fail here only on those three (proves nothing else depends on SDL2 types).
- [x] **4.3** `ImGuiRenderer.cs`: rename `BeginUpdate`→`BeginFrame`, `EndUpdate`→`EndFrame`, make `public`. No `[Obsolete]` forwarders (see §7). Update the XML docs that reference `Update(float, InputSnapshot)` (lines 385, 399) **and** line 410 (`SetPerFrameImGuiData`: "This is called by Update(float)"). Class becomes `partial`; summary line 12 ("Also provides functions for updating ImGui input") reworded to describe the `Add*Event` feed.
- [x] **4.4** New partial file `src/Veldrid.ImGui/ImGuiRenderer.Input.cs`: add `AddMousePosEvent`, `AddMouseButtonEvent`, `AddMouseWheelEvent`, `AddInputCharacter`, `AddKeyEvent`, `AddFocusEvent`, `SetDisplayFramebufferScale` — each a one-line forward to `ImGui.GetIO()` (or `_scaleFactor`). Add to `Veldrid.ImGui.csproj` only if the project uses explicit `<Compile>` items (it does not — SDK globbing picks it up).
- [x] **4.5** New project `src/Veldrid.ImGui.SDL2/Veldrid.ImGui.SDL2.csproj`: `PackageId = ArtificialNecessity.Veldrid.ImGui.SDL2`, `IsPackable=true`, same `AN.Veldrid.Build.props` import / `net9.0` / `Nullable=enable` / `ImplicitUsings=disable` / `LangVersion=latest` / `RootNamespace=Veldrid` as `Veldrid.ImGui.csproj`. `ProjectReference` → `..\Veldrid.ImGui\Veldrid.ImGui.csproj` and `..\Veldrid.SDL2\Veldrid.SDL2.csproj` (NOT StartupUtilities — it is not needed for the types). `Description`: "SDL2 InputSnapshot binding for ArtificialNecessity.Veldrid.ImGui. Reference implementation for other windowing hosts."
- [x] **4.6** `src/Veldrid.ImGui.SDL2/ImGuiRendererInputSnapshotExtensions.cs`: paste the deleted `UpdateImGuiInput` body as `FeedInputSnapshot(renderer, snapshot)` (replace `io.Add*` calls with `renderer.Add*` calls so it exercises the new public surface), add the `FeedInputSnapshot(renderer, snapshot, Sdl2Window window)` overload that additionally calls `renderer.AddFocusEvent(window.Focused)`, paste `TryMapKey` verbatim as `public static`.
- [x] **4.7** `git rm src/Veldrid.ImGui/version.json`. Regenerate `.stableapi` for both projects: `dotnet build Veldrid.sln /p:UpdateStableABI=true` (writes `src/Veldrid.ImGui/Veldrid.ImGui.stableapi` and creates `src/Veldrid.ImGui.SDL2/Veldrid.ImGui.SDL2.stableapi`). Eyeball the diff: `Update` removed, `BeginFrame`/`EndFrame`/`Add*`/`SetDisplayFramebufferScale` added, nothing else changed.
- [x] **4.8** Add `Veldrid.ImGui.SDL2` (and the test project from 4.9) to `Veldrid.sln` (`dotnet sln Veldrid.sln add …`, then nest under the `src` solution folder like the others). `cmd/publish-local.cs` packs the whole sln — no script change needed.
- [x] **4.9** New standalone test `src/Veldrid.ImGui.SDL2.Test/` (`OutputType=Exe`, `IsPackable=false`, references `Veldrid.ImGui.SDL2` + `Veldrid.StartupUtilities`): `VeldridStartup.CreateWindowAndGraphicsDevice` → `ImGuiRenderer` → loop `{ snapshot = window.PumpEvents(); renderer.BeginFrame(dt); renderer.FeedInputSnapshot(snapshot, window); renderer.EndFrame(); ImGui.ShowDemoWindow(); Render; SwapBuffers }` with `window.Resized += renderer.WindowResized`. Also call the plain `renderer.Update(dt, snapshot)` once so the drop-in path compiles. Accept `--frames N` to auto-exit for unattended runs. (There are no other in-repo callers of `ImGuiRenderer.Update(`.)
- [x] **4.10** `cmd/publish-local.cmd` → publishes both (same timestamp version). Arcane Siege: add `ArtificialNecessity.Veldrid.ImGui.SDL2` at `$(ANVeldridVersion)` to `Arcane.Client.csproj` — only until its own AN.Windowing adapter lands.

## 5. Acceptance criteria (gate on the Arcane roadmap)

- [ ] `Veldrid.ImGui.csproj` references only `Veldrid` + `ImGui.NET`. `dotnet list package --include-transitive` for `Veldrid.ImGui` shows no `Veldrid.SDL2`, no `Veldrid.StartupUtilities`, no SDL2 native.
- [ ] `grep -rP "InputSnapshot|Veldrid\.Sdl2|\bKeyEvent\b|\bKey\.|\bMouseButton\b" src/Veldrid.ImGui/` → zero hits. (Word boundaries are required: `ImGuiKey.` / `ImGuiMouseButton` legitimately appear in `ImGuiRenderer.Input.cs`.)
- [ ] `src/Veldrid.ImGui/version.json` no longer exists.
- [ ] `dotnet build Veldrid.sln` passes StableABI verification for `Veldrid.ImGui` and `Veldrid.ImGui.SDL2` (i.e. both `.stableapi` files are regenerated and committed).
- [ ] A consumer referencing only `Veldrid.ImGui` can drive a full frame with: `WindowResized` → `BeginFrame(dt)` → `Add*Event(...)` → `EndFrame()` → `ImGui.Begin/...End` → `Render(gd, cl)`.
- [ ] `Veldrid.ImGui.SDL2.Test` (4.9) runs: opens an SDL2 window, drives `imguiRenderer.Update(dt, window.PumpEvents())` + `FeedInputSnapshot(snapshot, window)` for a few frames with a visible `ImGui.ShowDemoWindow()`, exits cleanly. Manual check: mouse, wheel, text input, F-keys, arrows, modifiers, alt-tab focus loss.
- [ ] `Arcane.Client` compiles unchanged after adding `<PackageReference Include="ArtificialNecessity.Veldrid.ImGui.SDL2" Version="$(ANVeldridVersion)" />` (its existing `imguiRenderer.Update(dt, snapshot)` call resolves to the extension method).

## 6. Non-goals

- No changes to rendering, texture binding, shader assets, or `ColorSpaceHandling`.
- No AN.Windowing binding in this repo — that adapter lives with the host (Arcane / FluidUI.GameCompositor), and is a one-switch `ANWindowingKey → ImGuiKey` mapping plus `Add*Event` calls.
- No multi-viewport / docking-branch work.
- No fix to the CS8618 nullable warnings in `ImGuiRenderer.cs` (fields assigned in `CreateDeviceResources`, not the ctor) — pre-existing, unrelated.
- No repair of `.github/workflows/dotnet.yml` — it is dead upstream residue (`dotnet build src` with no sln in `src/`, pushes `ppy.*.nupkg`, .NET 8 SDK for a net9.0 repo). The real pipeline is `cmd/publish-local.cs` → `Veldrid.sln`.

## 7. Resolved questions (2026-09-22)

| Question | Decision |
|---|---|
| `[Obsolete] protected BeginUpdate/EndUpdate` forwarders? | **Drop.** Nothing in `C:\PROJECTS` subclasses `ImGuiRenderer`; the release is already a deliberate break. |
| `AddFocusEvent` now or defer? | **Include.** Part of the upstream backend contract; `ImGuiIOPtr.AddFocusEvent` exists in ImGui.NET 1.90.1.1. |
| Keep an `[Obsolete] Update(float, InputSnapshot)` shim in core? | **No** — it would keep the SDL2 type reference and defeat the purpose. |
| `src/Veldrid.ImGui/version.json`? | **Delete** (`git rm`). Nerdbank.GitVersioning leftover; `AN.Veldrid.Build.props` versioning is timestamp-based and reads no `version.json`. |
| How does Arcane version the new package? | Both packages are stamped with the same timestamp version by one `publish-local` run → `$(ANVeldridVersion)` covers `ArtificialNecessity.Veldrid.ImGui.SDL2` too. |
| Scope of the SDL2 example binding? | §3.2 three methods **plus** a focus example: `FeedInputSnapshot(this ImGuiRenderer, InputSnapshot, Sdl2Window window)` overload that calls `renderer.AddFocusEvent(window.Focused)` — so the reference binding demonstrates every `Add*` call. |
| Acceptance target for "existing SDL2 consumer"? | New standalone test project `src/Veldrid.ImGui.SDL2.Test/` (see 4.9). Named `.SDL2.Test` deliberately: it depends on Veldrid + ImGui + SDL2 to do anything. |

## 8. Reference: other in-tree facts checked during review

- No in-repo callers of `ImGuiRenderer.Update(` exist (no NeoDemo/samples; `Veldrid.LifetimeTests`/`BindingTests` don't touch ImGui). Only external consumer: `C:\PROJECTS\AN_ArcaneSiege\EngineSrc\Arcane.Client\Arcane.Client.csproj` (references `ArtificialNecessity.Veldrid.ImGui` + `StartupUtilities`).
- Publicly exposed members not listed in §3.1 "unchanged" but present and kept: `DestroyDeviceObjects()`, `CreateDeviceResources(gd, outputDescription)`, `CreateDeviceResources(gd, outputDescription, colorSpaceHandling)`, `GetImageResourceSet(IntPtr)`.
- `.stableapi` files are produced/verified by `StableABIVerifyTask` from `ArtificialNecessity.CodeAnalyzers` (`C:\PROJECTS\AN_CodeAnalyzers\src\StableABIVerification\`). Verify mode runs on every build and **errors on mismatch**; regenerate with `dotnet build /p:UpdateStableABI=true`.

## 9. Implementation notes (2026-09-22)

- **Native `SDL2.dll` for the test exe.** A `ProjectReference` to `Veldrid.SDL2` copies the native library to `native\win-x64\SDL2.dll` under the output dir (the NuGet `.targets` handles root placement only for package consumers), and `NativeLibraryLoader` does not search that subfolder → `TypeInitializationException` at `Sdl2Native`. `Veldrid.ImGui.SDL2.Test.csproj` therefore has a Windows-only `<None Link="SDL2.dll" CopyToOutputDirectory>` for `..\Veldrid.SDL2\native\win-x64\SDL2.dll`. Do not confuse with `Veldrid.SDL2.dll` (managed P/Invoke wrapper) or `Veldrid.ImGui.SDL2.dll` (this task's binding).
- **`/p:UpdateStableABI=true` is solution-wide.** It rewrote every project's `.stableapi` (all identical except `Veldrid.ImGui`) and *created* snapshots for the non-packable exe projects (`Veldrid.LifetimeTests`, `Veldrid.ImGui.SDL2.Test`); those two were deleted before commit, and a normal verify-mode build passes without them.
- **Threading.** ImGui.NET is a P/Invoke layer over cimgui's single global context; nothing was added for thread affinity. Contract (documented on `BeginFrame`): `BeginFrame` / `Add*Event` / `EndFrame` / `Render` on one thread. Hosts with off-thread input queue it themselves.
- Test run: D3D11, `--frames 120` auto-exit and an interactive windowed run both exited 0.