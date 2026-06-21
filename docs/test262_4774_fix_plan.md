# Test262 4,774-Test Remediation Plan

## Current Status (2026-06-21)

- Pass rate: **48,618/53,280 = 91.25%** → 4,662 remaining
- Phases complete: 0 (runner integrity)
- Phases in progress: 1 (crashes), 2 (parser), 6 (RegExp)
- Commits this session: 3

### Progress This Session

| Fix | Phase | Tests affected | Status |
|-----|-------|---------------|--------|
| `wellKnownIntrinsicObjects.js` harness | 1 | 9 unblocked | ✅ Committed |
| RegExp Symbol.split crashes (8) | 1 | 8 no longer crash | ✅ Committed |
| Set/Map ProxyObject casts (7) | 1 | 7 no longer crash | ✅ Committed |
| Arrow fn trailing commas | 2 | 2 passed, 3 parser→pass | ✅ Committed |
| Regex literal `\k<name>` validation | 2 | 7 pass (was 0) | ✅ Committed |

### Remaining Crash/Timeout Summary

- Crashes: 19→4 remaining (4 staging String crashes — UTF32/replace edge cases)
- Timeouts: 40 remaining (Promise 20, Array 14, RegExp 2, staging 4)
- Harness: 9→0 fixed

## Baseline

- Total: **53,280**
- Passed: **48,506**
- Non-passing: **4,774**
- Runtime errors: 4,476
- Parser errors: 230
- Timeouts: 40
- Crashes: 19
- Harness gaps: 9

## Execution Plan

| Phase | Scope | Current backlog | Exit condition |
|---|---|---:|---|
| 0 | Result/runner integrity | Tooling | Deterministic category reruns; no duplicate or missing results |
| 1 | Crashes, timeouts, harness | 68 | 0 crashes, timeouts, or harness-unsupported |
| 2 | Parser and early errors | 230 | Every parser-targeted category reaches 100% |
| 3 | Language semantics | 1,607 total | Classes, modules, loops, eval, assignments and environments reach 100% |
| 4 | Core built-ins | ~717 | Object model, Array, String, Function, Proxy, Promise and Iterator reach 100% |
| 5 | Binary data/concurrency | 518 | TypedArray, constructors, ArrayBuffer, DataView and Atomics reach 100% |
| 6 | RegExp | 250 | Full RegExp category reaches 100% without crashes |
| 7 | Temporal and Intl | 960 | All ECMA-402 and Temporal categories reach 100% |
| 8 | Annex B | 149 | Annex B reaches 100% |
| 9 | Staging | 573 | Staging reaches 100% |
| 10 | Tail sweep | Remaining categories | **53,280/53,280** |

## Phase Details

### 0. Runner Integrity

- Add a Windows PowerShell category runner with automatic chunking for large categories.
- Enforce `--timeout-ms 2000` and a 30-second stall watchdog.
- Ensure reruns replace, rather than overlap, previous result files.
- Make report generation detect duplicate test paths.
- Preserve the current 53,280-test manifest as the baseline.

### 1. Reliability First

- Fix 19 crashes: currently concentrated in RegExp and staging.
- Fix 40 timeouts: Promise 20, Array 14, staging 4, RegExp 2.
- Add `wellKnownIntrinsicObjects.js` support for the 9 harness failures.
- Add focused regression tests for every crash or timeout root cause.

### 2. Parser

Prioritize shared grammar seams:

1. Import attributes, import defer, import bytes and source-phase imports.
2. Top-level await and module declarations.
3. `using` and `await using`.
4. Class early errors, computed names, `super`, and arrow functions.
5. Remaining assignment-target and statement-list ambiguities.

### 3. Language Runtime

Work by shared runtime mechanism:

1. Class construction, `this`, `super`, private state and evaluation order.
2. Module linking, namespace objects, cycles and live bindings.
3. Iterator closing across `for-of`, destructuring and generators.
4. Lexical environments, direct/indirect eval and declaration conflicts.
5. Assignment, compound assignment, property ordering and abrupt completion.
6. Switch, try/finally, yield and async control flow.

### 4-8. Built-in Tracks

- Core objects: property descriptors, coercion, proxies, constructors and iterator protocols.
- Binary data: detachment, resizable buffers, length tracking, BigInt views and Atomics.
- RegExp: Unicode sets, prototype methods, named groups and property escapes.
- Temporal: ZonedDateTime first, then PlainYearMonth, PlainDateTime and Duration.
- Intl: NumberFormat, DateTimeFormat, Temporal integration, DurationFormat, ListFormat and locale handling.
- Annex B only after shared parser/object fixes have landed.

## Slice Workflow

Each production slice must:

1. Target one shared root cause, not one isolated test.
2. Add focused `FenBrowser.Js.Tests` coverage.
3. Build `FenBrowser.Js`.
4. Run the focused xUnit class.
5. Rerun only the affected Test262 category with the mandatory timeout/watchdog.
6. Continue until that category is **100%**, not merely above 95%.
7. Regenerate `docs/test262_results.md`.
8. Commit and push the verified component-sized change before starting another.

## Milestones

- **Normative milestone:** 51,798/51,798 excluding staging.
- **Final milestone:** **53,280/53,280 including staging**.
