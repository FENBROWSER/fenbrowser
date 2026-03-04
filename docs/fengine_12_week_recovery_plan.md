# FenEngine 12-Week Recovery Plan (Production-Grade Mandate)

**Date:** 2026-03-04
**Owner:** FenEngine Team
**Status:** Active

## Non-Negotiable Quality Bar

FenEngine work must meet this bar on every merge:

1. No half-baked features, no stubbed runtime paths, no known broken behavior merged behind silent failure.
2. All new runtime behavior must include regression tests (happy path + edge cases + failure path).
3. No new `async void` in engine/runtime code (true event-handler exceptions only).
4. No new silent `catch {}` in critical code paths (Core, Scripting, Bytecode VM/Compiler, Web APIs, Rendering pipeline).
5. No new generic `throw new Exception(...)` when a typed exception is possible.
6. No memory ownership ambiguity for native Skia objects in hot paths.
7. Code is not done until docs are synchronized in `docs/` and `docs/VOLUME_III_FENENGINE.md`.

## Mission

Raise FenEngine from current audit state to a production-grade baseline by fixing correctness gaps first, then reliability and security hardening, then performance.

## Exit Gates (Week 12)

1. `FenBrowser.FenEngine` build warnings reduced to less than 50.
2. Zero `NotImplementedException` in bytecode compiler/VM production paths.
3. Zero `async void` in engine/runtime code.
4. Silent `catch {}` removed from critical runtime paths.
5. Measurable Test262/WPT conformance increase from baseline.
6. 30-minute stress run without unbounded native memory growth.

## Work Phases

## Phase 1 (Weeks 1-3): Runtime Correctness and Error Discipline
- Remove untyped and silent error handling in critical runtime paths.
- Normalize asynchronous scheduling through controlled event-loop pathways.
- Establish deterministic behavior for storage, fetch, timer, and event dispatch paths.

## Phase 2 (Weeks 4-6): Bytecode/VM Completeness
- Eliminate current bytecode compiler unsupported-node failures in supported language surface.
- Remove VM hard-fails for AST-backed call/construct bridging.
- Add regression suites for language edge cases and exception semantics.

## Phase 3 (Weeks 7-9): Memory and Security Hardening
- Enforce deterministic ownership/disposal policy for Skia-native resources.
- Add parser/runtime/resource-limit enforcement tests for adversarial inputs.
- Validate thread-boundary discipline (UI vs render vs event loop microtasks).

## Phase 4 (Weeks 10-12): Performance, Soak, Release Gates
- Profile and optimize top CPU/allocation hotspots.
- Run long soak and stress scenarios with leak/crash regression checks.
- Lock release only when all exit gates pass.

## Current Sprint (Started 2026-03-04)

### Completed in this sprint
1. Converted `ImageLoader.LoadImageAsync` from `async void` to `Task` and updated fire-and-forget call sites to explicit discard.
2. Replaced silent exception swallow in SVG header sniff path with diagnostic logging.
3. Replaced `JavaScriptEngine` localStorage silent catches with warning logs.
4. Replaced localStorage persistence `async void` stub with `Task`-returning method.

5. Replaced silent catch wrappers in `JsZeroRuntime` with structured warning logs and explicit fallback return semantics in adapter boundary methods.
6. Replaced generic exceptions in `FetchApi` and `ModuleLoader` with typed exceptions (`InvalidOperationException`, `FenSyntaxError`, `FenInternalError`, `NotSupportedException`) in critical failure paths.
7. Added regression tests for fetch missing-handler rejection and module loader typed-failure behavior in `FenBrowser.Tests`.

8. Added safe runtime wrappers in `JavaScriptEngine` for host status/navigation, callback execution, and timer disposal to replace silent suppression in high-frequency paths.
9. Completed JavaScriptEngine hardening wave 4: removed all empty `catch {}` blocks in `JavaScriptEngine.cs`, added guarded callback/log wrappers, and switched fetch-handler setup to typed exceptions.

### Next immediate tasks
1. Burn down remaining engine-wide empty catch blocks (current scan: 31) with priority on `Core`, `Core/Bytecode`, `WebAPIs`, `Rendering` runtime paths.
2. Replace remaining generic `throw new Exception(...)` usages (current scan: 71) with domain-typed exceptions and wire failure-path tests.
3. Add regression tests for JavaScriptEngine callback/timer fault-isolation and storage/image async error paths; then enforce these checks in CI gating.




### Incremental Progress Update (2026-03-04, later pass)
- Completed an additional safety sweep across WebAPIs/Core/Rendering support paths.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 156 (from 167 before this pass group).
  2. Generic throw new Exception(...) reduced to 71 (from 74).
  3. async void remains 0 in FenEngine .cs scan.

### Incremental Progress Update (2026-03-04, BrowserHost sweep)
- Hardened BrowserHost event/log call boundaries in Rendering/BrowserApi.cs.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 129.
  2. Generic throw new Exception(...) remains 71.
  3. async void remains 0 in FenEngine .cs scan.


### Incremental Progress Update (2026-03-04, BrowserApi deep cleanup)
- Completed full empty-catch removal in `Rendering/BrowserApi.cs` and stabilized build/tests after mechanical cleanup.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 69.
  2. Generic throw new Exception(...) remains 71.
  3. async void remains 0 in FenEngine .cs scan.


### Incremental Progress Update (2026-03-04, Rendering pipeline sweep)
- Hardened render/layout core paths (`CustomHtmlEngine`, `MinimalLayoutComputer`, `NewPaintTreeBuilder`) by removing remaining silent catches in those files.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 52.
  2. Generic throw new Exception(...) remains 71.
  3. async void remains 0 in FenEngine .cs scan.


### Incremental Progress Update (2026-03-04, DOM bridge sweep)
- Hardened `JavaScriptEngine.Dom` by removing all remaining silent catches and adding guarded logging wrappers for DOM bridge operations.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 31.
  2. Generic throw new Exception(...) remains 71.
  3. async void remains 0 in FenEngine .cs scan.

### Incremental Progress Update (2026-03-04, full zero-empty-catch milestone)
- Completed final sweep over remaining FenEngine files and removed all residual empty catch blocks.
- Current metric deltas after this pass:
  1. Empty catch {} reduced to 0.
  2. Generic throw new Exception(...) remains 71.
  3. async void remains 0 in FenEngine .cs scan.
- Validation snapshot:
  1. dotnet build FenBrowser.FenEngine passed (0 errors).
  2. dotnet test FenBrowser.Tests currently shows 953 passed / 21 failed (pre-existing failures to be handled in next hardening stream).

### Incremental Progress Update (2026-03-04, typed-exception wave)
- Completed typed exception conversion for DOM wrappers, Browser API, JIT bootstrap, storage quota, and Test262 detach host hooks.
- Current metric deltas after this pass:
  1. Empty catch {} remains 0.
  2. Generic throw new Exception(...) reduced to 42.
  3. async void remains 0 in FenEngine .cs scan.
- Validation snapshot:
  1. dotnet build FenBrowser.FenEngine passed (0 errors).

### Incremental Progress Update (2026-03-04, VM + TypedArray typed-error wave)
- Converted VM and typed-array generic exceptions to typed errors with preserved message contracts.
- Current metric deltas after this pass:
  1. Empty catch {} remains 0.
  2. Generic throw new Exception(...) reduced to 20.
  3. async void remains 0 in FenEngine .cs scan.
- Validation snapshot:
  1. dotnet build FenBrowser.FenEngine passed (0 errors).

### Incremental Progress Update (2026-03-04, FenRuntime final generic-throw cleanup)
- Completed final conversion of remaining generic exceptions in FenRuntime.
- Current metric deltas after this pass:
  1. Empty catch {} remains 0.
  2. Generic throw new Exception(...) reduced to 0.
  3. async void remains 0 in FenEngine .cs scan.
- Validation snapshot:
  1. dotnet build FenBrowser.FenEngine passed (0 errors).

### Incremental Progress Update (2026-03-04, VM boundary compatibility)
- Resolved bytecode-only exception-type regression introduced by typed-error migration.
- Validation snapshot:
  1. Targeted bytecode contract tests: 4/4 passed.
  2. Full suite currently: 952 passed / 22 failed.

### Incremental Progress Update (2026-03-04, string/symbol conformance wave)
- Recovered string/symbol conformance regressions by fixing runtime prototype wiring and symbol primitive property lookup.
- Validation snapshot:
  1. Targeted conformance tests: 3/3 passed.
  2. Full suite currently: 955 passed / 19 failed.

### Incremental Progress Update (2026-03-04, Symbol constructor contract alignment)
- Updated integration assertion to match callable Symbol constructor semantics.
- Validation snapshot:
  1. Targeted Symbol integration test passed.
  2. Full suite currently: 957 passed / 17 failed.
