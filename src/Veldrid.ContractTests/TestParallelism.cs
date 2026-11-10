using Xunit;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Veldrid.ContractTests runs SERIALLY (no xUnit test-class parallelism). Decided Nov 10 2026 after the investigation recorded in
// _TASKS/GpuTimestampQueries.md section 8 ("Driver finding"):
//
//   Two Direct3D11 devices concurrently active in one process, at least one issuing timestamp queries, intermittently AccessViolate inside
//   ID3D11DeviceContext::FinishCommandList (NVIDIA RTX 4080, driver 32.0.16.2101). Reproduced with raw canonical Vortice query calls and
//   none of this fork's query code. Eliminated by direct experiment: any Veldrid shared state (there is none across devices), serialising
//   the bake (FinishCommandList), serialising the whole End(), serialising GetData, never re-using a query object, and the GC (300 forced
//   compacting collections on one device: 0/5 crashes; two devices with the GC suppressed via NoGCRegion: 4/5 crashes). Vulkan: never.
//
//   A per-class collection that only serialised the two timestamp classes against EACH OTHER was not enough: the second device may be any
//   other test's D3D11 device doing ordinary non-query work (observed 1/4 full-suite crashes with the collection in place). The suite takes
//   ~4 s serial, so there is nothing to lose. If a future driver fixes it, re-enable parallelism and re-run the suite 10x before trusting it.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
[assembly: CollectionBehavior(DisableTestParallelization = true)]