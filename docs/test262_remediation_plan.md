# Test262 Remediation Plan — closing the 17,717 failures

**Baseline (2026-06-08, full suite):** 35,629 / 53,346 passing = **66.79%**.
**Failing: 17,717.** Generated from the batched store (`Results/test262/batched/`);
re-rank any time with `python scripts/rank_failures.py` and classify reasons with
`python scripts/analyze_failures.py <category-glob>`.

This plan is ordered by **return on effort**, not by category size. The 17,717 are
highly concentrated: **the top ~24 batches hold 80% of all failures**, and several of
the biggest clusters are *runner/harness* bugs, not engine bugs — those come first
because one fix moves hundreds of tests across many categories.

## Working discipline (unchanged)
- One category at a time, drive to **≥95%**, then move on.
- Find the **shared root cause** in `failures[].details`, fix it once in the engine
  (or runner), don't patch tests one by one.
- Re-run only that category: `bash scripts/rerun-test262-category.sh <category>`, which
  refreshes `b_<tag>.json` and regenerates `docs/test262_results.md`. Commit the moved
  numbers with the fix.
- **RAM-safe runs**: large/flat dirs must go through `scripts/run-dir-chunked.sh`
  (process-per-chunk). Never run a 1000+ file dir as one process — it leaks to 25 GB+
  (see [test262-runner-ram-leak]; the underlying `RunWithPerTestTimeout` thread-abandon
  bug is Tier 4 below).

---

## Failure taxonomy (real counts, suite-wide)

| Cluster | Tests | Kind | Where it lands |
|---|---:|---|---|
| `$DONOTEVALUATE is not defined` | **1,373** | harness/runner | RegExp 192, literals 174, class/elements 152+152, … |
| `Temporal.*` undefined / unimplemented | ~3,400 | engine (greenfield) | built-ins/Temporal |
| `Intl.*` undefined / unimplemented | ~2,800 | engine (greenfield) | intl402 |
| Resizable/growable ArrayBuffer | **404** | engine (feature) | TypedArray 364, Atomics, ArrayBuffer |
| RegExp Unicode property escapes `\p{…}` | **394** | engine (feature) | RegExp 389, String 5 |
| `assert is not defined` | **217** | harness/runner | Object 193, String 15, … |
| `Stale heap handle` | **67** | engine (GC bug) | TypedArray 62, staging 2 |
| `assertRelativeDateMs` include | 6 | harness/runner | Date 6 |
| Class destructuring (`class/dstr`) | ~700 | engine (feature) | expr+stmt class/dstr c005–c007 |
| Long tail (`Cannot read properties of undefined`, RangeError, …) | remainder | mixed | spread across many categories |

---

## Tier 0 — Harness/runner fixes (do first; ~1,600 tests, days not weeks)

These are **not engine bugs** — the runner's harness prelude is missing globals that
test262 always provides, so otherwise-passing tests die at setup. One fix each, effect
spans every category.

### 0.1 Define `$DONOTEVALUATE` — ~1,373 tests
- **Evidence:** `ReferenceError: $DONOTEVALUATE is not defined` in 1,373 failures.
- **Cause:** test262 negative-parse tests reference the harness global `$DONOTEVALUATE`.
  The runner either (a) doesn't define it in the prelude, or (b) executes a
  `negative: { phase: parse }` test that should only be *parsed*.
- **Action:** in `FenBrowser.Js.Test262/Test262Runner.cs`, (1) add `$DONOTEVALUATE`
  to the harness prelude (`function $DONOTEVALUATE(){ throw new Test262Error("evaluated"); }`),
  and (2) verify negative-parse handling: a test with `negative.phase==="parse"` must be
  parsed only, passing iff the parser throws a SyntaxError. Some of these will then expose
  a *real* parser gap (engine fails to reject invalid syntax) — split those out and feed
  them into Tier 2.
- **Verify:** rerun `built-ins/RegExp`, `language/literals`, `language/*/class/elements`.

### 0.2 Define `assert` (callable + members) — ~217 tests
- **Evidence:** `ReferenceError: assert is not defined`, 193 in Object alone.
- **Cause:** test262's `assert.js` exposes `assert` as a *callable* with members
  (`assert.sameValue`, `assert.throws`, `assert.notSameValue`, …). The runner appears to
  provide the members but not the bare `assert(...)` callable (or doesn't load `assert.js`
  for these tests).
- **Action:** ensure the prelude registers `assert` as a function object carrying its
  members; confirm `assert.js` is in the always-loaded harness set in `Test262Runner.cs`.
- **Verify:** rerun `built-ins/Object` (expected ≈89.8% → ~95%+), `built-ins/String`.

### 0.3 Add `assertRelativeDateMs` include — 6 tests
- Add the harness include to the loadable set (same one-line pattern previously used for
  `decimalToHexString.js`). Low value but trivial; bundle with 0.2.

**Tier 0 expected:** ~+1,500 net passes, lifts Object and likely String over the gate,
and *removes harness noise from every other category's numbers* so Tier 1+ triage is clean.
**Re-run the full suite once after Tier 0** to get a true post-harness baseline before
deep engine work.

---

## Tier 1 — Near-gate categories (quick, after Tier 0 reveals the real misses)

Drive each of these to ≥95%; most of the remaining misses are a handful of real bugs once
harness noise is gone.

| Category | Now | After 0.x (est.) | Real work left |
|---|---|---|---|
| `built-ins/Object` | 89.8% | ~95%+ | `defineProperties`/`create`/`fromEntries` arg-validation edge cases |
| `built-ins/Array` | 91.3% | ~92% | 13 `timeout` cases (perf/looping), a few `undefined` reads |
| `built-ins/String` | 85.5% | ~90% | 5 `\p{}` (→ Tier 2.2), 15 `assert`, misc |
| `language/expressions/object` | 90.3% | ~92% | property-definition edge cases |
| `language/expressions/assignment` | 79.2% | — | destructuring assignment (overlaps Tier 2.1) |

---

## Tier 2 — Bounded engine features (shared root cause; high yield)

### 2.1 Class destructuring — ~700 tests
- **Where:** `language/expressions/class/dstr` + `language/statements/class/dstr`
  (chunks c005–c007 ~15–18% pass), plus assignment destructuring overlap.
- **Action:** destructuring patterns in class-method parameters / static blocks. Fix in the
  parser + bytecode compiler (`FenBrowser.Js/Parser`, `FenBrowser.Js/Bytecode`). The two
  dirs mirror each other, so one fix doubles.
- **Verify:** `scripts/run-dir-chunked.sh language/expressions/class/dstr language/statements/class/dstr`.

### 2.2 RegExp Unicode property escapes `\p{…}` — ~394 tests
- **Evidence:** `SyntaxError: Invalid pattern … Unknown property`.
- **Action:** implement `\p{Script=…}`/`\p{General_Category=…}`/binary properties in the
  regex compiler (`FenBrowser.Js/Regex/`). Generated unicode tables already exist there —
  wire up the property lookup. Also handle `Unrecognized escape sequence` (33) and invalid
  group-name cases in the same pass.
- **Verify:** rerun `built-ins/RegExp` (39.1% → target 95%); also lifts String.

### 2.3 Resizable / growable ArrayBuffer — ~404 tests
- **Evidence:** `TypeError: ArrayBuffer is not resizable`.
- **Action:** implement `ArrayBuffer({maxByteLength})`, `resize()`, `SharedArrayBuffer.grow()`,
  and length-tracking TypedArrays over resizable buffers (`FenBrowser.Js/Builtins` +
  the typed-array/heap backing). Touches TypedArray, TypedArrayConstructors, Atomics, ArrayBuffer.
- **Verify:** rerun those four categories.

### 2.4 `Stale heap handle` GC bug — ~67 tests
- **Evidence:** `TypeError: Internal heap error: Stale heap handle.` (TypedArray 62).
- **Cause:** same family as the prior GC self-sweep bug ([gc-stale-handle-self-sweep]) — a
  handle used after its cell moved/freed during a collection in typed-array paths.
- **Action:** pin the in-flight handle across allocation/GC in the typed-array builtins.
  This is a **correctness bug**, fix regardless of count.

---

## Tier 3 — Greenfield long-haul tracks (the ~6,200-test majority)

These are genuine "implement the feature" projects; schedule as standalone milestones, not
quick wins.

### 3.1 Temporal — ~3,653 tests (built-ins/Temporal 20.6%)
- Largely unimplemented; no single lever. Build out incrementally by type:
  `PlainDate` → `PlainTime` → `PlainDateTime` → `Duration` → `ZonedDateTime` → `Instant` →
  `Calendar`/`TimeZone`. Land each type to its own ≥95% before the next.

### 3.2 Intl (intl402) — ~2,852 tests (14.6%)
- Build per Intl object: `NumberFormat`, `DateTimeFormat`, `Collator`, `PluralRules`,
  `Segmenter`, etc. (`FenBrowser.Js/Intl/`). `Intl.Locale`/`RelativeTimeFormat` already
  partially done — extend the same BCP-47 plumbing.

### 3.3 Module code — 516 tests (`language/module-code`, 31.7%)
- Module semantics: namespace objects, import/export binding, dynamic `import()`,
  `import.meta`, cyclic resolution (`FenBrowser.Js/Modules/`).

---

## Tier 4 — Runner correctness (removes the need to chunk)

- **`RunWithPerTestTimeout` thread abandonment** (`Test262Runner.cs:1469`): a timed-out
  test's worker thread keeps running and pins its `JsHeap`, the live-memory leak behind the
  25 GB blow-ups. Fix: run each test on a droppable dedicated thread whose heap becomes
  collectable on abandonment, and/or make regex/native loops honour `InterruptCallback`.
  Removes the chunking workaround and stops wedged native-regex tests from stalling batches.

---

## Sequencing summary

1. **Tier 0** harness fixes (`$DONOTEVALUATE`, `assert`, includes) → ~+1,500, clean triage.
2. **Full-suite re-run** → true post-harness baseline.
3. **Tier 1** near-gate categories to ≥95% (Object, Array, String, expressions/object).
4. **Tier 2** bounded features — class destructuring, regex `\p{}`, resizable AB, stale-heap GC.
5. **Tier 4** runner thread-abandon fix (any time; unblocks unattended full runs).
6. **Tier 3** Temporal / Intl / modules as long-haul milestones.

**Rough reachable target:** Tiers 0–2 (+ tail cleanup) put the suite in the low-to-mid
**80s%** without touching Temporal/Intl; closing those two is what takes it toward the 90s.

### Tracking
- Per-category truth: `docs/test262_results.md` (regenerated on each category rerun).
- Re-rank remaining work: `python scripts/rank_failures.py`.
- Reason breakdown for a category: `python scripts/analyze_failures.py "built-ins_Object"`.

---

## Progress log

### 2026-06-08 — Tier 0 harness + two bounded engine fixes
- **Tier 0.2 `assert`** (runner): tests whose own body only referenced `Test262Error`
  but which pulled a harness include (e.g. `propertyHelper.js`, which calls `assert`
  internally) were served the *minimal* prelude with no `assert`. `BuildRuntimeHarnessPrelude`
  now takes `hasIncludes` and uses the full prelude whenever includes are present.
  `$DONOTEVALUATE` also defined in both preludes. **built-ins/Object 89.8% → 95.5%** (gate cleared).
- **RegExp prototype accessors** (22.2.6): `dotAll`/`global`/`source`/`flags`/… were
  per-instance data props; spec wants accessor getters on `%RegExp.prototype%`. Added
  `InstallRegExpFlagAccessors` + `EscapeRegExpPattern`; stripped instance data props from
  all construction paths (kept `lastIndex`). **built-ins/RegExp 39.1% → 43.3%**.
- **Resizable ArrayBuffer** (Tier 2.3, partial): `IsResizable` was derived from
  `MaxByteLength > ByteLength`, so `new ArrayBuffer(N,{maxByteLength:N})` reported
  non-resizable and `resize()` threw — breaking the whole `testTypedArray` harness fan-out.
  Now tracked with an explicit construction-time flag. **built-ins/TypedArray 31.1% → 51.4%**,
  TypedArrayConstructors 56.2% → 58.7%, ArrayBuffer → 70.6%.
- **Overall 66.79% → 67.94%.**

**Still open on the `$DONOTEVALUATE` cluster:** defining the global does *not* convert the
1,373 parse-negative tests — they need the **parser/regex-compiler to actually reject the
invalid syntax** (then `JsParserException` → pass). That's Tier 2 parser-strictness work.

**Next on TypedArray (703 fails left):** out-of-bounds-after-resize validation (the
"assert.throws: no error thrown" cluster across set/slice/map/filter — a length-tracking
view over a shrunk resizable buffer must throw), the `Stale heap handle` GC bug (74,
Tier 2.4), and BigInt-array harness conversions (83).
