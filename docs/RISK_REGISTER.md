# FenBrowser — Risk Register

> Auto-generated Gate 0 Reality Audit. Last refreshed: 2026-06-27.
> Format: Risk ID | Description | Likelihood | Impact | Mitigation | Status

## Architecture Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| A1 | Monolithic process: renderer crash kills browser UI | High | High | Per-tab renderer processes (Gate 10); FEN_PROCESS_ISOLATION=brokered exists but not default | OPEN |
| A2 | No network broker: renderer has direct network access | Medium | High | Network broker process (Gate 10); handler pipeline exists in-process | OPEN |
| A3 | Image decode in-process: malformed image can crash renderer | Medium | Medium | Image decoder worker (Gate 10); not yet implemented | OPEN |
| A4 | JS/DOM GC interaction: wrapper identity, cycles, cross-heap references not fully documented | Medium | High | MEMORY_MODEL.md needed (Gate 2); HostObjectTable + HandleScope already mitigate | OPEN |
| A5 | Dual script engine legacy: FEN_BROWSER_SCRIPT_ENGINE=legacy switch causes split-brain (WebDriver) | High | Medium | Deprecate legacy engine completely; FenJS is sole engine | MITIGATED (FenJS default) |
| A6 | No structured IPC schema for all messages | Medium | Medium | IPC_MODEL.md (Gate 2); IPC fuzzing harness exists | OPEN |

## Security Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| S1 | No renderer sandbox — malicious page can access filesystem | Medium | High | Process isolation + sandbox (Gates 10-11) | OPEN |
| S2 | CSP enforcement may be incomplete | Low | Medium | CspPolicy handler in pipeline; needs audit | MITIGATED |
| S3 | CORS enforcement may be incomplete for complex cases | Medium | Medium | Handler in pipeline; preflight partial | PARTIAL |
| S4 | Mixed content not fully blocked | Low | Medium | Not verified | OPEN |
| S5 | No COOP/COEP/CORP enforcement | Medium | Medium | Not implemented | OPEN |

## Conformance Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| C1 | JS engine regression from new feature work | Medium | High | test262 CI gate; per-feature verification; batched runs | MITIGATED |
| C2 | Layout regression from new CSS/formatting context work | Medium | Medium | Browser unit tests; screenshot comparisons; reftests | PARTIAL |
| C3 | Test262 runner RAM leak (>25GB) on large flat directories | Medium | Low | Batched scripts; chunked runs; --skip flag | MITIGATED |
| C4 | Stale test262 results in docs (not re-run after fixes) | High | Medium | docs/test262_results.md is source of truth; batched results folder | MITIGATED |
| C5 | WPT runner broken by script engine or WebDriver changes | Medium | Medium | WPT runner uses legacy engine mode; dual-engine split | MITIGATED (workaround) |

## Integration Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| I1 | Script loading order bugs cause SPA boot failures | High | High | Script loading trace (Gate 1); per-script lifecycle events | OPEN |
| I2 | Event loop timing bugs (microtasks before DOM operations) | High | High | Event loop trace (Gate 1); SPEC_EVENT_LOOP.md exists | PARTIAL |
| I3 | Promise microtask checkpoint not running at correct HTML spec points | Medium | High | Event loop trace; DOM/JS integration tests | OPEN |
| I4 | WebIDL binding errors misclassified as JS engine failures | Medium | Medium | Missing API tracker; WPT per-API testing | OPEN |
| I5 | CSSOM/getComputedStyle returns wrong values, breaking framework layout calculations | Medium | High | Style dump infrastructure; element inspection commands | OPEN |

## Performance Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| P1 | No incremental layout — full relayout on any change | Medium | Medium | Incremental layout (Gate 12); dirty flag system exists | PARTIAL |
| P2 | No incremental style — full restyle on any change | Medium | Medium | Incremental style invalidation (Gate 12) | OPEN |
| P3 | Bump-pointer arena allocation may overflow under heavy pages | Low | Medium | FrameArenaPool; canary+poison detects in DEBUG | MITIGATED |
| P4 | GC pauses may cause jank on heavy DOM pages | Medium | Medium | Generational GC in JS engine; .NET GC tuning; profiling needed | OPEN |

## Dependency Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| D1 | SkiaSharp API changes or deprecation | Low | High | ISvgRenderer/ITextMeasurer/IRenderBackend adapters isolate Skia | MITIGATED |
| D2 | HarfBuzzSharp API changes | Low | Medium | ITextMeasurer adapter isolates HarfBuzz | MITIGATED |
| D3 | Silk.NET (GLFW) API changes or platform-specific bugs | Medium | Medium | Headless mode exists; multi-backend possible | PARTIAL |
| D4 | .NET runtime GC behavior change | Low | Medium | JS engine has its own GC; arena allocator avoids .NET heap for hot paths | MITIGATED |

## Operational Risks

| ID | Risk | Likelihood | Impact | Mitigation | Status |
|----|------|-----------|--------|------------|--------|
| O1 | Debugging requires attaching debugger — no self-contained trace bundles | High | High | Diagnostic spine (Gate 1); trace.jsonl output; summary.md per site | OPEN |
| O2 | Missing APIs silently fail — no automated detection | High | High | Missing API tracker (Gate 1); IHostHooks logging | OPEN |
| O3 | Real-site regression goes undetected between changes | High | Medium | Real-site smoke testing (Gate 4); screenshot comparisons | OPEN |
| O4 | Test262 runner hangs on slow tests without timeout | Low | Medium | --timeout-ms 2000 mandatory; stall-kill watchdog in scripts | MITIGATED |
