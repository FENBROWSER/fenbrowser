# FenBrowser.Core Completion Goal

## Objective
Bring `FenBrowser.Core` to production-grade baseline with spec-aligned behavior, hardened security defaults, deterministic diagnostics, and zero known placeholder/stub paths in active runtime flows.

## Definition Of Complete
1. `FenBrowser.Core` build passes with no Core warnings caused by dead code, fake async, unreachable branches, or known footguns.
2. All Core network/file/cors/csp policy paths are fail-closed and covered by focused tests.
3. Parser/tokenizer/tree-builder high-risk correctness warnings are removed or converted to explicit, tested behavior.
4. Core logging/diagnostics emit actionable, non-misleading signals only.
5. Every tranche is pushed with verification evidence.

## Execution Plan (In Progress)
1. Security and policy hardening in `ResourceManager` and handlers.
2. Runtime/code-quality debt burn-down in Core (dead fields, unreachable paths, bad lifecycle methods, fake async).
3. Parser/tokenizer stabilization and warning elimination.
4. Core-only verification matrix and remaining gap closure.

## Current Status
- In progress.
- Recent completed slices:
  - HTTP cache semantics hardening + tests.
  - File-scheme policy enforcement in Core fetch paths + tests.
  - Supported-scheme enforcement and Core async/runtime cleanup + tests.
