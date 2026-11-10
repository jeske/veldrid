# BUGFIX - STATUS: UNRESOLVED (parked, avoided) - D3D11: two devices in one process + timestamp queries -> AccessViolation inside FinishCommandList

> **STATUS: UNRESOLVED.** Root cause not fixed. The crash is AVOIDED (tests run serially; production is single-device) and placed, by elimination,
> inside the NVIDIA D3D11 user-mode driver. Nine untested hunches are listed in section 6 for whoever picks this up.

Filed Nov 10 2026 while landing `_TASKS/GpuTimestampQueries.md` (GPU timestamp queries + automatic GPU timing).
Root cause is placed, by elimination, inside the NVIDIA Direct3D11 user-mode driver. This note packages the observations, every workaround we tried
(and whether it helped), and the hunches we did NOT get to test, so the next person can pick it up without re-deriving anything.

Companion: spec section 8 "Driver finding" in `_TASKS/GpuTimestampQueries.md` (shorter; this note is the long form).

## 1. Environment

| | |
|---|---|
| GPU / driver | NVIDIA GeForce RTX 4080, driver 32.0.16.2101 (Nov 2026) |
| OS | Windows 11 10.0.26200 |
| Runtime | .NET 8, Vortice.Direct3D11 2.4.2 (Vortice.Windows), xunit 2.9.2 test host |
| Fork state | commits `7812365c` .. `ffed657c` (timestamp primitive, automatic GpuTiming layer, bake locks) |
| Reproducers | `TimestampQueryTests` + `GpuTimingAutoTests` run in parallel with the rest of `Veldrid.ContractTests` (before `TestParallelism.cs`); the temporary two-thread probes described in section 4 (deleted, easily rewritten from section 6) |

## 2. Symptom

The xunit test host dies with:

```
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at Vortice.Direct3D11.ID3D11DeviceContext.FinishCommandList(SharpGen.Runtime.RawBool, Vortice.Direct3D11.ID3D11CommandList ByRef)
   at Veldrid.D3D11.D3D11CommandList.EndCore()
   at Veldrid.CommandList.End()
   at <whatever test is ending a command list at that moment>
```

- Intermittent: 25 %-100 % of runs depending on the configuration (rates per configuration in section 4).
- Always the SAME native frame: `ID3D11DeviceContext::FinishCommandList` on a DEFERRED context - the CPU-side step that closes a recording and
  bakes it into an `ID3D11CommandList`. Nothing has been submitted (`ExecuteCommandList`) for that recording yet; the submit path was never on
  the crashing stack. ("Recording" in older notes meant this CPU-side build, not submission.)
- Which test/thread crashes varies; the one on the stack is simply whoever is baking when the corruption is detected.
- Never seen with a single `GraphicsDevice` in the process, in any configuration we tried (section 4 rows 1-2).
- Never seen on Vulkan (row 12).

## 3. Minimal shape of the trigger

Two threads, **each with its own `GraphicsDevice.CreateD3D11(...)`** (so two `ID3D11Device`s, two immediate contexts, two deferred contexts -
nothing shared at the API level), each looping ~200 times:

```
cl.Begin()
cl.WriteTimestamp(pool, 0)             // ID3D11DeviceContext::End(ID3D11Query TIMESTAMP) on the deferred context, inside a TIMESTAMP_DISJOINT bracket
8 x cl.CopyBuffer(src, dst, 64 MB)     // or fill-rate draws; anything that keeps the GPU busy for ~ms
cl.WriteTimestamp(pool, 1)
cl.End()                               // FinishCommandList  <-- AV surfaces here, on either thread
device.SubmitCommands(cl)              // ExecuteCommandList on that device's immediate context
(optionally) poll TryResolveTimestamps // GetData on that device's immediate context until S_OK
```

Remove the timestamps and it never crashes. Run only one of the two threads and it never crashes. The second device does NOT need to use
queries itself (row 13): a second device doing ordinary non-query work alongside a query-using device is enough.

## 4. Experiment record (every row was run; "pass" = 0 crashes in N runs)

| # | devices | what is concurrent / what changed | result |
|---|---|---|---|
| 1 | 1 | heavy copies + timestamps, results never read back | pass 3/3 |
| 2 | 1 | row 1 + a second thread forcing ~300 **compacting Gen2 GCs** during the run (80 KB filler blocks allocated before/around our objects, a random third freed, `GC.Collect(2, Forced, blocking, compacting)` in a loop) | pass 5/5 |
| 3 | 2 | heavy copies, **no queries** | pass 3/3, later 4/4 |
| 4 | 2 | heavy copies + timestamps, read back each iteration | **AV 2/3** |
| 5 | 2 | heavy copies + timestamps, never read back | AV 3/3 before the "never re-issue an unfetched query" rule; pass 4/4 after it |
| 6 | 2 | **raw Vortice**: `Begin(disjoint) / End(ts0) / copies / End(ts1) / End(disjoint)` recorded straight on the deferred context, `GetData` straight on the immediate context, **none of this fork's query code** | **AV 2/4** |
| 7 | 2 | row 4 + process-wide static lock around `FinishCommandList` | **AV 4/4** |
| 8 | 2 | row 7 + `GetData` under the same static lock | AV 3/4 |
| 9 | 2 | row 7 + a query object is NEVER re-issued (fresh `ID3D11Query` created for every write, old one Released - pending command list holds its own COM ref) | AV 3/5 |
| 10 | 2 | whole ContractTests with the ENTIRE `CommandList.End()` (render-pass end + disjoint End + bake) under one static lock | AV 2/8 |
| 11 | 2 | row 4 with the **GC suppressed** (`GC.TryStartNoGCRegion(240 MB)` held across the whole run; `EndNoGCRegion` confirmed no GC happened) | **AV 4/5** |
| 12 | 2 | rows 3/4/5 on **Vulkan** (same code, same test harness) | pass 4/4, 4/4, 4/4 |
| 13 | 2+ | whole ContractTests, the two timestamp classes serialised against each other but in parallel with the other 93 tests (other D3D11 devices doing non-query work) | AV 1/4 |
| 14 | 1 at a time | whole ContractTests with assembly-level `DisableTestParallelization` | pass 127/127 x 9 |

## 5. Things we tried as workarounds, and what each told us

| workaround | kept? | effect on the crash | what it rules out |
|---|---|---|---|
| Never re-issue an `ID3D11Query` whose last result was not fetched with `GetData` (rename the slot's query; drop unfetched disjoint windows) | yes (`D3D11TimestampQueryPool.MarkWritten`, `D3D11GraphicsDevice.ReturnTimestampDisjointWindow`) | fixed the light-load never-read-back case (row 5); no effect on the read-back case | "re-issuing a query before its result was consumed" is not the trigger on its own |
| Process-wide static lock around `FinishCommandList` (D3D11) | yes, as an approved conservative measure (comment in `D3D11CommandList.EndCore` carries the rationale) | none (row 7) | the bake step is not itself the unsafe region - `FinishCommandList` is where corruption is DETECTED |
| Same lock around `vkEndCommandBuffer` (Vulkan) | yes, conservative parity | n/a (Vulkan never crashed) | - |
| `GetData` under the same lock as the bake | no (experiment only) | none (row 8) | the readback is not racing the bake |
| Never re-issue ANY query object, even fetched ones | no (experiment only) | none (row 9) | query-object lifetime is not the trigger |
| Lock the whole `CommandList.End()` | no (experiment only) | none (row 10) | the end-of-recording sequence (pass end, disjoint `End`, bake) is not the unsafe region |
| Force compacting GCs on one device | n/a (experiment) | cannot provoke it (row 2) | a GC moving memory a native call believed pinned is not SUFFICIENT |
| Suppress the GC entirely on two devices | n/a (experiment) | still crashes (row 11) | the GC is not NECESSARY either - it is out of the picture |
| Serialise only the two query-using test classes against each other | no - insufficient (row 13) | reduced but not eliminated | the second device need not use queries |
| Serialise the whole test assembly (`TestParallelism.cs`) | **yes - the working mitigation** | eliminated (row 14) | - |
| Audit for Veldrid static state shared across devices | n/a | none found: the D3D11 backend's only statics are a feature flag and a debug-name counter; `ID3D11CommandList`s are created per `End()` and disposed after `ExecuteCommandList`, never pooled; staging buffers are per command list | not a Veldrid cross-device sharing bug |

## 6. What we believe (and how confident we are)

**High confidence - supported by rows 3, 6, 12, 13, 14:**
- The corruption happens inside the NVIDIA D3D11 user-mode driver (`nvwgf2umx.dll`), the one component two independent devices share.
- Trigger shape: two D3D11 devices concurrently active in one process, at least one of them issuing queries (timestamp + disjoint) in deferred
  command lists under load. Row 6 (raw API, no Veldrid query code) is the decisive one for "not our code".
- It is not fixable from the Veldrid layer, only avoidable.

**Medium confidence - consistent with the data but not isolated:**
- The ingredient is the QUERY path, not timestamps specifically: the disjoint `Begin/End` bracket and `End(ts)` are the only differences between
  rows 3 and 4. We never tested occlusion/event queries; if they also trigger it, any multi-device D3D11 app using queries is affected.
- The window is during RECORDING of the other device (between `Begin()` and `End()`), not during its bake or submit: locking the bake, the whole
  `End()`, and the readback all failed (rows 7-10), while a device that does no concurrent recording (row 1) never crashes. Hunch: the UMD keeps
  a process-wide (not per-device) structure for pending query slots that is touched during `End(query)` recording on a deferred context and
  read during `FinishCommandList`; two devices recording concurrently race on it.

**Hunches NOT tested (ordered by how cheap they are):**
1. Lock the whole RECORDING - i.e. one process-wide lock held from `Begin()` to `End()` - to confirm the window is recording-time. Trivial to do in
   the base `CommandList` as a diagnostic; if it eliminates the crash it pins the window exactly (but is not an acceptable production fix).
2. Lock only `WriteTimestamp` (`DeviceContext.End(query)` and the disjoint `Begin/End`) across devices. If THAT alone fixes it, the unsafe
   structure is specifically the query-slot allocation at record time, and a targeted static lock there would be an acceptable production fix.
3. Occlusion (`D3D11_QUERY_OCCLUSION`) and event (`D3D11_QUERY_EVENT`) queries instead of timestamps - does any query type trigger it?
4. `D3D11_CREATE_DEVICE_SINGLETHREADED` on one of the two devices (our tests never set it) - does the UMD take a different, safe path?
5. The D3D11 debug layer (`GraphicsDeviceOptions.Debug = true`): it may validate/serialise differently and either mask or name the problem.
6. Microsoft WARP (`D3D_DRIVER_TYPE_WARP`) instead of the NVIDIA driver: if WARP never crashes, that is the cleanest proof it is the NVIDIA UMD.
7. A second GPU vendor (AMD / Intel) - same test, different UMD.
8. A pure-Vortice console app with no Veldrid at all (~40 lines: two `D3D11CreateDevice`, two deferred contexts, the row-6 loop) for an NVIDIA
   bug report. Row 6 already ran the raw calls, but on Veldrid-created devices/contexts, so this is the last formality.
9. Older/newer NVIDIA drivers.

## 7. Why it matters / who is exposed

- NOT exposed: any process with a single `GraphicsDevice` - AN_Monsters, FluidUI GameCompositor (its multiple threads all record/End/Submit on
  ONE device, which is the never-crashing single-device case), ArcaneSiege.
- Exposed: tests and tools that create several D3D11 devices and keep them active concurrently - `Veldrid.ContractTests` under xunit parallelism
  was the only real instance, and it is now serialised. A future "one device per OS window" design on D3D11 would need to revisit this.

## 8. Pointers

- Code: `src/Veldrid/D3D11/D3D11CommandList.cs` (`EndCore`, the bake lock and its rationale), `src/Veldrid/D3D11/D3D11TimestampQueryPool.cs`
  (`MarkWritten` renaming rule), `src/Veldrid/D3D11/D3D11GraphicsDevice.cs` (`ReturnTimestampDisjointWindow`, `TryResolveTimestampsCore`),
  `src/Veldrid.ContractTests/TestParallelism.cs`.
- Spec: `_TASKS/GpuTimestampQueries.md` section 8.
- Related finding from the same day (not a crash): on this driver `CopyBuffer` work is not bracketed proportionally by graphics-pipeline
  timestamps (copy engine); timing tests use fill-rate draws. Same spec section.