# FenBrowser Risk Register

Last updated: 2026-05-05

This register tracks active engineering risks that materially affect correctness, maintainability, or verification trust.

| Risk | Severity | Area | Why it matters | Current signal | Fix plan |
|---|---|---|---|---|---|
| Site-specific compatibility logic in engine paths | High | Rendering, scripting, host defaults | Can hide root causes and create non-portable behavior | `scripts/ci/run-code-cleanup-audit.ps1` reports site markers in engine paths | Isolate remaining behavior under documented quirk boundaries, add reduced tests, and remove once generic pipeline behavior closes gaps |
| Oversized god files in hot pipeline surfaces | High | Runtime, CSS, rendering, parser | Large files increase regression probability and slow safe iteration | Audit reports top files with very high line counts (`FenRuntime.cs`, `BrowserApi.cs`, `CssLoader.cs`, `Parser.cs`) | Apply characterization tests, then extract one responsibility per patch without behavior drift |
| Incomplete broad standards conformance | High | HTML/CSS/JS compliance | Limits predictability on real web content and blocks trustworthy claims | Engine status remains Partial/Experimental in multiple pipeline areas | Continue staged capability closure with spec mapping, focused tests, and baseline evidence bundles |
| Diagnostic-only quirks without timely removal | Medium | Compatibility layer | Temporary quirks can become permanent coupling | Active entries tracked in `QUIRKS.md` | Enforce removal condition reviews and refuse quirk expansion without reduced-test evidence |
| Verification debt between focused slices and full suites | Medium | CI/test strategy | Green focused runs may mask broad regressions | Full-suite/manual flows are decoupled from commit-time gates | Keep commit-time gates strict for invariants; schedule broader regression runs and track failures by category |
| Logging/placeholder semantics in runtime paths | Medium | Runtime and host behavior | Placeholder fallbacks can obscure missing behavior under load | Cleanup audit still surfaces placeholder/stub markers | Triage by risk: keep legitimate placeholders explicit, remove fake fallbacks, and convert ambiguous cases into deterministic failure states or tested behavior |

## Operating Rules

1. Every new high-severity risk needs an owner path and a concrete closure plan.
2. Risks resolved by tests/docs should be downgraded or closed in the next update.
3. Do not claim risk closure without deterministic evidence.
