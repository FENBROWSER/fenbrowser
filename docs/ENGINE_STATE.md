# FenBrowser Engine State

Live Gate 0 snapshot: 2026-07-14; completion audit revalidated 2026-07-15. Original audit base: `e3fa7a41bfd5f803d086f1bf27412c0a879eb90e` on branch `new`, with unrelated user changes present in the working tree. This file reports observed state; it does not turn implemented code into a completion claim.

Allowed status values in this tracker are the execution-system status values. `DONE` is intentionally unused.

## Audit method

The reality audit proceeds in this order:

1. Confirm compile inclusion from each `.csproj`; a source file that is excluded is not an implemented runtime capability.
2. Identify the owning project and active call path.
3. Build the smallest owning surface.
4. Run included focused tests that can falsify the claim.
5. Check local conformance stores instead of remote sources.
6. Run `debug-site` and inspect the fresh screenshot, lifecycle, script, event-loop, network, style/layout, and raw trace artifacts.
7. Record an implementation status only with a code path; record `TESTED` only with a named result; reserve `DONE` for the full acceptance contract.

## Solution inventory

| Project | Responsibility observed in the current checkout | Status | Evidence |
| --- | --- | --- | --- |
| `FenBrowser.Core` | DOM, parser, networking primitives, logging, security and platform contracts | INTEGRATED | Referenced by FenEngine, Host, WebDriver, and Tooling; Release build passed |
| `FenBrowser.FenEngine` | CSS, Box Tree, layout, Paint Tree, raster orchestration, FenJS browser bridge | INTEGRATED | Google bundle reached style/layout/paint/raster; Release build passed |
| `FenBrowser.Host` | Window, tabs, input, renderer-child entrypoint, process-isolation coordinators | INTEGRATED | Host is in the Tooling dependency graph; brokered path is opt-in |
| `FenBrowser.DevTools` | Native DevTools and remote-debug protocol surfaces | IMPLEMENTED | Project builds; no fresh DevTools behavior run in this audit |
| `FenBrowser.Js` | ECMAScript parser/compiler/interpreter/runtime/heap | TESTED | Local Test262 source of truth reports 49,070/53,198 (92.24%) |
| `FenBrowser.Js.Tests` | JavaScript unit tests | IMPLEMENTED | Project exists; full suite was not rerun in this slice |
| `FenBrowser.Js.Test262` | Local Test262 runner | TESTED | Batched results generate `docs/test262_results.md` |
| `FenBrowser.Js.Fuzz` | JavaScript fuzz harness | IMPLEMENTED | Project builds in the solution; no fresh fuzz campaign result |
| `FenBrowser.Js.Shell` | Standalone JavaScript shell | IMPLEMENTED | Project is in the solution; no fresh shell smoke |
| `FenBrowser.WebIdlGen` | WebIDL parser/generator CLI | IMPLEMENTED | Ten IDL inputs and generator code exist; generated outputs are excluded from FenEngine compilation |
| `FenBrowser.WebDriver` | Automation protocol implementation | IMPLEMENTED | Project builds; no fresh end-to-end WebDriver run |
| `FenBrowser.Tooling` | `debug-site`, WPT orchestration, performance and diagnostic commands | TESTED | Current Release dependency-graph build passed with 0 errors and 498 warnings |
| `FenBrowser.Conformance` | Conformance support project | IMPLEMENTED | Project builds through focused test dependency graph |
| `FenBrowser.Tests` | Browser integration/unit tests | TESTED | Focused diagnostic/process filter passed 44/44; important directories remain excluded |

## Browser integration state

| Subsystem | Current status | Confirmed behavior | Unclosed behavior |
| --- | --- | --- | --- |
| Navigation/document lifecycle | TESTED | Google transitioned Requested -> Fetching -> ResponseReceived -> Committing -> Interactive -> Complete | Cross-frame and failure-path coverage is not current |
| HTML parsing/DOM construction | INTEGRATED | Current Google produced a populated DOM dump with 568 elements | html5lib baseline is not current |
| Classic script loading | TESTED | Google discovered 14 script elements with 18 completed executions and no script execution failure | Execution/discovery count semantics need normalization; modules were not exercised |
| Event loop/timers/microtasks | TESTED | Current Google fired DOMContentLoaded/load, recorded 9 microtask checkpoints, and reports zero callback failures consistently | Broader repeating-timer, navigation-invalidation, and rAF reductions remain |
| Missing API observation | TESTED | Dedicated tracker tests passed and Google emitted missing-property observations | Bundle export loses rich fields and reports site expandos as APIs |
| Fetch/network visibility | INTEGRATED | Google captured 25 requests | One request is counted as failed; CORS/cookie/security decisions are not summarized |
| CSS/style/layout/paint | TESTED | Google styled 569 nodes, built 184 boxes and 98 paint nodes, and captured a usable screenshot | Input interaction and screenshot comparison are not automated; the frame exceeded budget |
| WebIDL-generated bindings | IMPLEMENTED | Parser and generator exist | `FenBrowser.FenEngine.csproj` explicitly removes `Bindings/Generated/**/*.cs`; runtime exposure remains manual |
| DOM host bindings | INTEGRATED | Manual FenJS host dispatch supports Google boot plus tested local focus/type/input/change/submit behavior and live checkable-input checkedness | The monolithic dispatch path lacks generated conversions, overload resolution, and complete brand/descriptor coverage |
| Per-tab renderer process | IMPLEMENTED | Brokered coordinator, renderer child, authenticated IPC, shared-memory frames, crash policy exist | Default mode is in-process; a current brokered real-site proof is absent |
| Network process | IMPLEMENTED | Child host, session, coordinator, capability token and payload limits exist | No active caller of `NetworkCoordinator.SendAsync` was found; fallback uses in-process `HttpClient` |
| GPU/utility child targets | INTEGRATED | Target sessions auto-start in brokered mode; compositor submissions are wired | Raster/composite isolation and recovery are not acceptance-tested here |
| Storage service process | NOT_STARTED | Page-local storage service exists inside FenEngine | No separate storage-service process boundary |
| Image decoder worker | NOT_STARTED | In-process image support exists | No isolated hostile-image decoder worker |

## Diagnostic spine state

| Capability | Status | Evidence or gap |
| --- | --- | --- |
| Structured NDJSON and trace JSONL | INTEGRATED | `EngineLog` emits both formats; many session/document/realm IDs are still null in the Google trace |
| `debug-site <url> [settle_ms]` | TESTED | Current Google, example.com, and `fen://performance` bundles exist |
| `debug-site-interact <url> <target_selector> <text> <submit_selector> ...` | TESTED | Local form bundle has ordinary click/focus/type/submit, terminal navigation, event records, and before/after screenshots |
| Lifecycle, script, event-loop, network snapshots | INTEGRATED | Present in the Google bundle |
| DOM/style/layout/paint/display-list dumps and screenshot | INTEGRATED | Present in the Google bundle |
| Deterministic first-causal-blocker classifier | TESTED | `first_blocker.json` models 19 ordered milestones; fixture tests and Google report `none` while unattempted interaction remains explicit |
| Exception attribution | TESTED | Typed callback records, `event_loop.json`, `exceptions.json`, trace, and summary share one drained snapshot; the current Google bundle agrees at zero |
| Missing API bundle schema | STUBBED | Runtime tracker has provenance; bundle exports a smaller `EngineCapabilities` view with false positives |
| `ipc.json` | NOT_STARTED | Not in the artifact manifest |
| `sandbox_denials.json` | NOT_STARTED | Not in the artifact manifest |
| `performance.json` | NOT_STARTED | Render telemetry exists but is not exported as the required artifact |
| Individual dump/selector CLI commands | NOT_STARTED | Current Tooling usage exposes only `diagnose` and `debug-site` for this workflow |

## Primary real-site target

Google is the current target because it has the freshest complete bundle and already crosses the boot pipeline. The 2026-07-15 run loaded the document, completed 18 script executions without a direct script failure, fired lifecycle events, built DOM/style/layout/paint state, and rendered the main UI. The previously attributed eight timer failures shared one cause: the baseline JIT implementation of the ECMAScript `in` operator routed a DOM host handle through JS heap-object decoding. A later attributed Promise rejection in `script-5` function `k0c` exposed a second general boundary defect: FenJS iterator acquisition ignored a valid `HTMLCollection` host prototype, and the manual DOM surface did not define the collection's WebIDL iterator. Host collections now preserve their opaque handle and acquire a live `Symbol.iterator` through the validated prototype path. The current Google bundle reports zero direct script failures, zero callback failures, and zero exceptions. Navigation transition samples are explicitly historical; current lifecycle and event-loop fields agree on complete/DCL/load. The selector-driven Tooling interaction command now proves the deterministic local form through click, focus, typing, submit, terminal navigation, bounded event records, and before/after screenshots. The next acceptance work is running the same generic command on Google; missing-API classification remains separately open.

Evidence: `logs/real-site/www.google.com/20260715T085915Z/`.

## Gate status

| Gate | Status | Exit evidence still required |
| --- | --- | --- |
| Gate 0 reality audit | RESEARCHED | Fresh full unit/html5lib baselines and smoke coverage beyond the current sites |
| Gate 1 diagnostic spine | INTEGRATED | Lifecycle agreement, rich API classification, listener/promise diagnostic reductions, and IPC/sandbox/performance artifacts |
| Gate 2 architecture freeze | RESEARCHED | Human decisions recorded for memory ownership, default process policy, network fallback, and IPC versioning |
| Gate 3 build/test/trace infrastructure | INTEGRATED | Make diagnostic/process tests discoverable and establish repeatable selected WPT baselines |
| Gate 4 real-site boot pipeline | TESTED | Google input/submit/network behavior and one regression-protected reduction |

## Persistent file disposition for this audit

| Requested first-execution output | Owning file |
| --- | --- |
| 1. Current engine audit plan | `ENGINE_STATE.md` and `TEST_BASELINE.md` |
| 2. Diagnostic spine implementation plan | `DIAGNOSTICS.md` |
| 3. Real-site failure classification plan | `REAL_SITE_DEBUGGING.md` |
| 4. Minimal smoke-test matrix | `REAL_SITE_TRACKER.md` |
| 5. Required trace/log points | `DIAGNOSTICS.md` |
| 6. Missing API tracker format | `MISSING_API_TRACKER.md` |
| 7. Process boundary audit | `ARCHITECTURE.md`, `PROCESS_MODEL.md`, and `IPC_MODEL.md` |
| 8. Current architecture risk register | `RISK_REGISTER.md` and `BLOCKERS.md` |
| 9. Top dependency-ready tasks | `NEXT_TASKS.md` |
| 10. Files created or updated | This section and `INDEX.md` |

| File group | Files | Disposition |
| --- | --- | --- |
| Gate 0 state | `ENGINE_STATE.md`, `TEST_BASELINE.md`, `KNOWN_GAPS.md`, `RISK_REGISTER.md`, `NEXT_TASKS.md` | Updated from current code and local artifacts |
| Gate 1 and site attribution | `DIAGNOSTICS.md`, `REAL_SITE_DEBUGGING.md`, `REAL_SITE_TRACKER.md`, `MISSING_API_TRACKER.md` | Created or updated with the current bundle contract and Google triage |
| Gate 2 boundary audit | `ARCHITECTURE.md`, `PROCESS_MODEL.md`, `IPC_MODEL.md`, `SECURITY_MODEL.md`, `MEMORY_MODEL.md`, `NATIVE_INTEROP_MODEL.md`, `BLOCKERS.md`, `DECISION_RECORDS/README.md` | Created; unresolved contracts are explicitly blocked |
| Capability trackers | `JS_ENGINE_TRACKER.md`, `WEBIDL_BINDINGS_TRACKER.md`, `DOM_API_TRACKER.md`, `EVENT_LOOP_TRACKER.md`, `SCRIPT_LOADING_TRACKER.md`, `NETWORK_FETCH_TRACKER.md`, `CSS_LAYOUT_TRACKER.md`, `PERFORMANCE_DASHBOARD.md` | Created with observed state and next evidence |
| Navigation | `INDEX.md` | Updated to link the live execution state |
| Canonical subsystem volumes | `VOLUME_I_SYSTEM_MANIFEST.md` through `VOLUME_VI_EXTENSIONS_VERIFICATION.md` | No audit-only change required; this slice changes no runtime behavior or subsystem boundary |
