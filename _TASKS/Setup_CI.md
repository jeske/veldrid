# Set up CI for AN_Veldrid

**Status:** TODO (not started)  
**Origin:** Noted during `_TASKS/ImGui_Decouple_From_SDL2.md` review (2026-09-22)

## Background

The inherited upstream workflow `.github/workflows/dotnet.yml` (from `ppy/veldrid`) was **deleted** on 2026-09-22 because every step was broken for this fork:

| Upstream step | Why it was dead here |
|---|---|
| `setup-dotnet` → `8.0.x` | All projects target `net9.0` |
| `dotnet restore src` / `dotnet build src` / `dotnet pack src` | No `.sln`/`.csproj` directly in `src/` — the solution is `Veldrid.sln` at repo root |
| `dotnet nuget push bin\Packages\Release\ppy.*.nupkg` with `secrets.NUGET_API_KEY` | Packages are `ArtificialNecessity.*`; the secret was ppy's |
| `softprops/action-gh-release` with `ppy.*.nupkg` | Glob matches nothing |

The real pipeline is `cmd/publish-local.cs` → `dotnet build/pack Veldrid.sln -c Release` → local NuGet feed (`LOCAL_NUGET_REPO`), per the Build System policy in `_TASKS/Veldid_Fluid_Fork_Plan.md` ("builds publish to local NuGet repos, not CI-to-nuget.org").

## Scope

Build-verification CI only. **No publish step** — publishing stays local by policy until the `ArtificialNecessity.Fluid.Gpu` rename/public release (see Fluid Fork Plan, Milestone 5) decides otherwise.

## Steps

- [ ] `.github/workflows/build.yml`: `windows-latest`, `actions/setup-dotnet@v4` with `dotnet-version: 9.0.x`, `dotnet build Veldrid.sln -c Release /nodeReuse:false`.
- [ ] Verify the `ArtificialNecessity.CodeAnalyzers` package (StableABI verification, analyzers) is resolvable on the runner — it comes from a local feed today. Options: publish it to nuget.org under the reserved `ArtificialNecessity.*` prefix, or a GitHub Packages feed, or a `nuget.config` with a checked-in `packages/` fallback.
- [ ] Run `Veldrid.LifetimeTests` / `Veldrid.BindingTests` if they can run headless (check backends available on the runner — D3D11 WARP may work, Vulkan/GL likely not).
- [ ] `Veldrid.ImGui.SDL2.Test --frames 3` is a windowed exe — probably not runnable on CI; document as manual-only or skip.
- [ ] Consider a `macos-latest` job for the Metal binding compile (no run).
- [ ] Triggers: `push` to `master`, `pull_request` to `master`. No tag-based publish.

## Open questions

- Is GitHub Actions the right host, or should this wait for the repo rename to `AN_Fluid_Gpu` (workflow files survive a rename, so no blocker either way)?
- Where does `ArtificialNecessity.CodeAnalyzers` come from on a clean runner? This is the only real blocker.