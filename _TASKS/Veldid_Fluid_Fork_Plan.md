# SPEC-FluidRebrand: Veldrid Fork → ArtificialNecessity.Fluid.Gpu

- **Status:** Draft
- **References:** [Veldrid (upstream)](https://github.com/veldrid/veldrid), [ppy/veldrid](https://github.com/ppy/veldrid), [NuGet package ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation)

- [ ] Milestone 1 — Repo renames (`AN_Fluid_Gpu`, `AN_Fluid_UI`) + fork detach
- [ ] Milestone 2 — README/description rewrite (successor framing, lineage, compat notice)
- [ ] Milestone 3 — Migrate Gpu-layer specs from Fluid repo into `AN_Fluid_Gpu`
- [ ] Milestone 4 — Courtesy notice to Veldrid Discord/mellinoe
- [ ] Milestone 5 — NuGet package `ArtificialNecessity.Fluid.Gpu` published

## Overview

The `AN_Veldrid` repo is a substantially diverged fork of Veldrid (38 commits, ~150 files, ~16.7k additions past ppy/veldrid). Divergence spans three axes: a rewritten shader compiler producing cross-platform precompiled shader bundles (a breaking API change at "2.x" scale), a replaced build system (see Build System), and in-tree work specs (`_TASKS/`). Upstream `veldrid/veldrid` is effectively dormant (~70 lines of change in 6 months; the maintainer's README states he can no longer publicly share updates), and `ppy/veldrid` is a single-purpose mobile-focused fork with no upstreaming path.

GitHub suppresses forks: they are excluded from code search by default, carry a "forked from" banner that misframes the work as derivative, and rank poorly. The remedy is the standard hard-fork-with-new-identity pattern (Jenkins/Hudson, MariaDB/MySQL): detach, rename, publish under owned package IDs, credit lineage.

This spec defines the naming scheme, what changes, and — deliberately — what does not: the rebrand is identity-layer only. **Code namespaces are untouched.**

## Naming Scheme

Three tiers, each fit to its context:

| Tier | Convention | Example | Rationale |
|---|---|---|---|
| GitHub repo | `AN_*` with `_` separators | `AN_Fluid_Gpu` | House style; org URL (`github.com/ArtificialNecessity/`) supplies context |
| NuGet package ID | `ArtificialNecessity.*` with `.` separators | `ArtificialNecessity.Fluid.Gpu` | Context-free, self-describing; prefix already reserved on nuget.org |
| Code namespace | `ArtificialNecessity.*` for originated code; **inherited namespaces kept** | `Veldrid.*` (this fork) | See Namespace Policy |

Mechanical rule: **underscore in repo name ⇔ dot in package ID.** Either name is derivable from the other without a lookup table.

`Fluid` is a pure brand namespace — no package exists at the bare root. Layers are leaves:

```
ArtificialNecessity/AN_Fluid_Gpu   → ArtificialNecessity.Fluid.Gpu    (this fork: GPU abstraction, graphics + compute)
ArtificialNecessity/AN_Fluid_UI    → ArtificialNecessity.Fluid.UI     (FluidUI toolkit; repo renamed from AN_FluidUI)
ArtificialNecessity/AN_Audio       → ArtificialNecessity.Audio        (outside Fluid brand — deliberate: not on the GPU stack)
(future)             AN_Fluid_Dom  → ArtificialNecessity.Fluid.Dom    (FlowDOM; evocative names stay colloquial, suffixes stay boring)
(future)             AN_Audio_Midi → ArtificialNecessity.Audio.Midi
```

The GPU layer ships as **one package** — graphics and compute share one API surface (`GraphicsDevice`, resource model, shader-bundle pipeline); splitting into `.Graphics`/`.Compute` would cut through one codebase or force a hidden `.Core` package. `Gpu` is the accurate noun for both.

## Namespace Policy (What Does NOT Change)

The fork keeps `Veldrid.*` as its code namespace, and all type names (`GraphicsDevice`, `CommandList`, `ResourceFactory`, …) stay as-is.

- Zero churn across the 150-file diff and every consuming project — consumers change one `<PackageReference>` line and no code.
- `using Veldrid;` is self-documenting lineage; upstream Veldrid docs/examples/answers remain ~valid for the unbroken majority of the API.
- Package ID and root namespace are formally independent in .NET; matching them is a convention for new code, not forks. Precedent: Jenkins shipped `hudson.model.*` packages for over a decade post-rename.

`ArtificialNecessity.*` namespaces apply to code David originates (Fluid.UI, Audio, future work). Inherited namespaces keep their inheritance.

## Build System

The fork replaces upstream's build with the house build system. Two policies define it:

1. **`Directory.Build.props` is user config, not repo config.** It is `.gitignore`d and disallowed as a build input. Rationale: MSBuild resolves the nearest `Directory.Build.props` walking upward, so a repo that depends on one cannot be safely composed (nested/vendored) inside another tree — the outer tree's props silently shadow or collide with the inner repo's expectations. Treating the file as the *user's* machine-level config keeps every repo self-contained and composition-safe.
2. **Builds publish to local NuGet repos.** Package flow between ecosystem projects goes through local feeds, not project references across repo boundaries.

Both policies get a short README statement (a contributor's first encounter with the `.gitignore`d props file should find the explanation, not a surprise). The build replacement is also the strongest single upstream-blocker — an API change is reviewable in principle; a replaced build system never merges — and is recorded as such under Alternatives Considered.

## In-Tree Documentation

The repo carries work specs in `_TASKS/` (shader-bundle implementation spec, per-bug fix docs for SPIRV-Cross HLSL varying packing, Vulkan draw ResourceSet refcounting, Metal precompiled shader binding, Metal stencil). Most divergence work was spelunk-and-fix and is documented at the bugfix level; the major design spec (shader bundler) is already in-tree.

**Migration task (small):** three Veldrid-layer specs live in neighboring repos and move into `AN_Fluid_Gpu` at rename time:
- `AN_SilkyNvg/plans/veldrid_image_flags_sampler_support.md`
- `AN_SilkyNvg/plans/veldrid-example-refactor.md`
- `AN_FluidUI/_SPECS/Veldrid_SetStencilReference_Spec.md`

Rule for placement: a spec lives where the API surface it describes lives; cross-layer specs go with the lower layer, with a pointer from the upper. This keeps the successor repo documentation-self-contained — the same composition principle behind the `Directory.Build.props` policy.

## Repo & Package Changes

### AN_Veldrid → AN_Fluid_Gpu

1. **Rename** the repo. GitHub preserves issues/stars/history and auto-redirects the old URL.
2. **Detach the fork** (GitHub support "Virtual Support Assistant → detach fork" request, or fresh-push the history to a new repo). Removes the "forked from ppy/veldrid" banner and enables default code-search indexing. Rename and detach are independent steps; both are required.
3. **Repo description** (indexed hardest by GitHub search — the lineage sentence lives here, not only in the README):
   > GPU abstraction for .NET (Vulkan/Metal/D3D11/OpenGL) — substantially rewritten successor to Veldrid, with cross-platform precompiled shader bundles.

### AN_FluidUI → AN_Fluid_UI

Rename only, for repo↔package symmetry. Redirects make this near-free. "FluidUI" survives as the colloquial/project name; `ArtificialNecessity.Fluid.UI` is the package ID.

### README Requirements (AN_Fluid_Gpu)

Four load-bearing statements:

1. **Lineage credit:** based on Veldrid by Eric Mellino (mellinoe); substantially rewritten — new shader pipeline, cross-platform precompiled shader bundles, breaking API changes.
2. **Compatibility notice:** *Not API-compatible with upstream Veldrid 4.x.* Prevents the one genuinely bad outcome (assumed drop-in compatibility).
3. **Namespace notice:** code namespace remains `Veldrid.*` for API continuity; do not install alongside upstream Veldrid packages (namespace collision).
4. **Support posture:** maintained for the Artificial Necessity ecosystem; PRs welcome, support not promised.
5. **Build policy:** `Directory.Build.props` is user config and is not a build input (see Build System); builds publish to local NuGet feeds.

### NuGet

- Package ID `ArtificialNecessity.Fluid.Gpu` — automatically covered by the existing `ArtificialNecessity.*` prefix reservation, as is the entire future tree.
- Tag the package `veldrid` — NuGet tags carry search weight; this is the discovery path for "maintained Veldrid" searches, since the name deliberately contributes nothing to it.

## Courtesy Notice

One message to the Veldrid Discord / mellinoe before flipping public, roughly: *"I have a substantially rewritten Veldrid — new shader-bundle pipeline, breaking API changes at a 2.x level. Releasing under a new name (ArtificialNecessity.Fluid.Gpu) with lineage credit; happy to discuss if you'd rather see it become Veldrid's future."* Sender is a known contributor (patch accepted March 2026). Either answer unblocks; do not block on a reply.

## Open Questions

- Monorepo vs. separate repos for Gpu + UI: separate repos chosen for now (the Veldrid-successor identity needs its own discoverable repo/issue tracker — the founding problem of this spec). Revisit if the majority of commits turn out to cross the Gpu/UI boundary, where a monorepo's atomic cross-layer commits would win.
- Whether/when `AN_Audio` joins the Fluid brand — currently deliberately outside it. Decide before any public Audio release; renames get expensive once external docs reference the name.

## Alternatives Considered

- **Upstream PR against veldrid/veldrid** — mechanically trivial (~70 lines of upstream drift since March) but unreviewable at 16.7k additions by a dormant project, and the replaced build system makes the tree unmergeable regardless of review bandwidth; a PR is not a vehicle for succession.
- **`ArtificialNecessity.Veldrid` / `AN_Veldrid` (status quo name)** — legally fine, standard prefixed-fork convention, but signals "maintained variant," underselling a 2.x-scale successor.
- **`FluidGfx` / `FluidGPU` as standalone brand** — FluidGfx had the best unique-token/search properties; both required contesting a bare `Fluid` prefix on NuGet and lacked the family hierarchy. The `ArtificialNecessity.Fluid.*` scheme keeps the accurate `Gpu` noun as a leaf at zero ownership cost.
- **Splitting `.Graphics` / `.Compute` packages** — artificial cut through one API surface; deferred unless a genuinely separate compute layer emerges.
- **Renaming the code namespace to match the package** — worst available trade: 150+ file churn, orphans the code from the entire Veldrid documentation/knowledge base, zero functional gain.