# FenJS Gap Audit — 2026-05-28

Snapshot of every known gap in the FenBrowser JS engine (`FenBrowser.Js/`) as of commit `0ec6b59f`, measured by:

- Fresh test262 runs against `external/test262` (parser subset 500, runtime subset 2000)
- `dotnet test` on `FenBrowser.Js.Tests` (2,354 green) and `FenBrowser.Js.Fuzz` (35 green)
- File-by-file structural audit (engine: 36,780 LOC across `FenBrowser.Js/`)
- JIT, IC, realm-isolation, builtin module review

All gaps below carry a **scope estimate** (S = ≤1 day, M = 2–5 days, L = >1 week), a **first commit** decomposition (smallest production-grade unit per the standing engineering directives), and **spec/test references** so the work is reviewable.

The engineering directives (`fenjs-engineering-directives` memory) apply to every item: spec-first, smallest-complete-unit per commit, build clean, tests added (incl. negative + regression), no `Co-authored-by` trailers, no unapproved deps, no broken builds committed.

---

## 0. Measured baseline (verify before claiming work shifted any number)

| Metric | Value | Source |
|---|---|---|
| Engine LOC | 36,780 | `FenBrowser.Js/` |
| Largest file | `BytecodeInterpreter.cs` — 15,306 LOC | structural audit |
| Unit tests green | 2,354 / 2,354 | `dotnet test FenBrowser.Js.Tests` |
| Fuzz tests green | 35 / 35 | `dotnet test FenBrowser.Js.Fuzz` |
| Test262 parser subset | 492 / 500 = 98.4% | `--parser-subset --max 500` |
| Test262 runtime subset | 824 / 2,000 = 41.2% | `--runtime-subset --max 2000 --timeout-ms 15000` |
| Test262 crashes (runtime subset) | 5 | runtime subset, see §1 |
| Test262 timeouts (runtime subset) | 2 | runtime subset, see §5 |

Runtime-subset failure distribution by top directory:

| Failing dir | Count |
|---|---|
| `annexB/language/eval-code` | 343 |
| `built-ins/ArrayBuffer/prototype` | 138 |
| `annexB/language/global-code` | 112 |
| `annexB/language/function-code` | 107 |
| `built-ins/Array/prototype` | 104 |
| `annexB/built-ins/String` | 86 |
| `annexB/built-ins/RegExp` | 54 |
| Other | 408 |

**Important caveat:** The runtime subset is the first 2,000 tests alphabetically, which is heavy on `annexB/*` and `built-ins/A*`. A full ~50K-test sweep would shift the distribution; this audit's prioritization should be re-checked after a full run.

---

## 1. CRASH FIXES — 5 known runtime-subset crashes (Priority: 🔴 highest)

**Scope:** S. Bounded, high-signal: a production engine must never crash on conformant input.

| # | Test | Crash message | Suspect subsystem |
|---|---|---|---|
| 1 | `annexB/language/statements/for-of/iterator-close-return-emulates-undefined-throws-when-called.js` | `Stale heap handle.` | `JsHeap` handle lifecycle around iterator-close completion |
| 2 | `built-ins/AggregateError/errors-iterabletolist-failures.js` | `Stale heap handle.` | Iterator collection on error path |
| 3 | `built-ins/ArrayBuffer/allocation-limit.js` | `Array dimensions exceeded supported range.` | Unbounded `new byte[len]` in ArrayBuffer ctor — needs spec RangeError |
| 4 | `built-ins/ArrayBuffer/data-allocation-after-object-creation.js` | `Array dimensions exceeded supported range.` | Same root cause as #3 |
| 5 | `built-ins/Array/from/iter-map-fn-err.js` | `Stale heap handle.` | Heap handle invalidated when iterator map fn throws |

**First commits (one per crash, ordered cheapest first):**

1. **ArrayBuffer huge-length RangeError (#3, #4)** — guard `ArrayBufferBuiltin` ctor + `transferToFixedLength` so excessive `length` arguments throw spec RangeError before `new byte[len]`. Single commit, covers two tests.
   - Spec: ECMA-262 25.1.3.1 `ArrayBuffer ( length [ , options ] )` step 4 — `byteLength` must be a non-negative integer ≤ implementation-defined limit; otherwise throw RangeError.
   - Files: `FenBrowser.Js/Builtins/ArrayBufferBuiltin.cs`
   - Tests: add 2 unit tests for huge / negative length → RangeError; the two test262 files will then pass.

2. **Iterator-close stale handle (#1, #2, #5)** — investigate `JsHeap` handle lifecycle around `IteratorClose` / `IteratorAbruptCompletion`. Hypothesis: when an inner iterator's `return` callback throws or when `from`'s map fn throws, the surrounding handle is freed before the abrupt-completion path reads it back. Reproduce locally, add a handle-pin in the close path.
   - Spec: ECMA-262 7.4.6 `IteratorClose ( iteratorRecord, completion )` — completion must be preserved across the close call.
   - Files: `FenBrowser.Js/Interpreter/BytecodeInterpreter.cs` (iterator close paths), `FenBrowser.Js/Heap/JsHeap.cs`.
   - Tests: 3 unit tests reproducing each crash + 1 regression test for the general "abrupt completion across iterator close" invariant.

**Definition of Done for §1:** runtime-subset crash count = 0; new tests green; no regressions in 2,354-test unit suite; commits separate per directives.

---

## 2. INTERPRETER MONOLITH BREAKUP — 15,306-line file (Priority: 🟠 high)

**Scope:** L. `BytecodeInterpreter.cs` is the single biggest maintainability risk; ~74% of all opcode handlers + all builtin-installation code + IC glue + iterator helpers all live in one file. C.5–C.9 extracted Math; everything else is still inline.

**Why it matters:** large files mean conflicting commits during high-velocity conformance work, slow reviewer comprehension, hard to unit-test individual handler families. The directives explicitly call out "architecturally clean, modular".

**Proposed extraction order (each step = one commit, behaviour-preserving partial-file split):**

| # | Slice | Target file | Est. LOC moved |
|---|---|---|---|
| 1 | Property access (`GetPropByName`, `SetPropByName`, `DeletePropByName`, `GetReceiverProperty`, `SetReceiverProperty`, `GetElem`, `SetElem`, `DeleteElem`) | `BytecodeInterpreter.Properties.cs` | ~900 |
| 2 | Arithmetic + comparison + logical opcodes (`Add` through `BitNot`, `Eq` through `Ge`, `LogicalNot`, `Typeof`, `In`, `InstanceOf`) | `BytecodeInterpreter.Operators.cs` | ~700 |
| 3 | Call / construct / spread (`CallFunction`, `CallMethod*`, `ConstructFunction`, `CallSpread`, `BoundFunctionObject` dispatch) | `BytecodeInterpreter.Calls.cs` | ~600 |
| 4 | Iterator/for-of/for-in helpers (`CreateForOfIterator`, `IteratorClose`, etc.) | `BytecodeInterpreter.Iterators.cs` | ~400 |
| 5 | Class/private-field/super opcodes (`DefineGetterByReg`, `DefineSetterByReg`, `LoadSuperProp`, `GetPrivateField`, `SetPrivateField`, `DefinePrivateField`, `BrandCheck`) | `BytecodeInterpreter.Classes.cs` | ~500 |
| 6 | Generator / async resumption (`Yield`, `YieldStar`, `Await`, resume dispatch) | `BytecodeInterpreter.Generators.cs` | ~450 |
| 7 | Environment / declaration instantiation (`InstantiateVarDeclarations`, `InstantiateLexicalDeclarations`, `EnsureGlobalObject`, scope ops) | `BytecodeInterpreter.Environments.cs` | ~600 |
| 8 | Exception handling (`Throw`, try/catch/finally dispatch, native-error constructors) | `BytecodeInterpreter.Exceptions.cs` | ~400 |

**Hard rules per the directives applied to refactors:**
- Each slice is a pure file move + `partial class` split. No semantic change. Zero conformance delta expected. Run full unit + fuzz suite after every slice.
- No mixing of slice work with conformance fixes in the same commit.
- If a slice surfaces a latent bug, file a separate gap entry — don't bury the fix in the move.

**Target end state:** `BytecodeInterpreter.cs` ≤ 4,000 LOC (dispatch loop, frame plumbing, top-level Execute, JIT entry).

---

## 3. JIT MATURATION — beyond expression-tree dispatch (Priority: 🟡 medium)

**Current state** (`FenBrowser.Js/Bytecode/JitCompiler.cs`, 858 LOC):
- Tier-up threshold: 100 invocations (`JitCompiler.TierUpThreshold`).
- Path 1: constant-fold via abstract interpretation. Wins on pure-constant functions only.
- Path 2: System.Linq.Expression tree mirroring the interpreter switch case-by-case, then `Expression.Compile()` to a delegate.
- Coverage: 141 opcode cases in JIT vs 115 in interpreter — full opcode set covered.
- Inline caches (`PropertyInlineCache`, 4-entry polymorphic, shape-based) are NOT inlined into the JITted code — the JIT calls back into the same `GetReceiverProperty` helper the interpreter uses.

**Gaps:**

### 3.1 IC fast paths inlined into JITted code (M)
Today a hot `obj.x` access in JITted code still pays a virtual call into `GetReceiverProperty` + IC lookup + slot read. Inlining the IC fast path (shape check + array index) directly into the emitted expression tree typically cuts property-access overhead 3–5×.
- First commit: emit shape-check + slot load for `GetPropByName` only, monomorphic case only. Fall through to interpreter helper on miss. Reuse `PolymorphicInlineCache.TryGet` shape; emit comparison against `_e0.Shape` only.
- Spec: not spec-bound; perf only.
- Tests: extend `InlineCacheTests` with a JIT-mode parameterised case; add a microbenchmark in `FenBrowser.Js.Test262` differential harness.

### 3.2 Tier-up policy beyond invocation count (S)
`Invocations >= 100` triggers compile. Misses loops in long-running functions (one invocation, millions of iterations).
- First commit: add per-function back-edge counter; trigger tier-up on `(invocations * 100 + backedges) >= threshold`. Counter increment goes on `Jump`/`JumpIfFalse` opcodes when the target IP < source IP.
- Tests: 2 unit tests — pure-loop function tiers up at first call after N iterations; pure-call-count function tiers up at N calls.

### 3.3 OSR (on-stack replacement) — deferred (L)
Real OSR (switching from interpreter to JITted code mid-execution) is a large effort. Track as future work, not in scope for this gap doc's first wave.

### 3.4 Native IL emit for arithmetic — deferred (L)
Expression-tree codegen is already a step away from raw IL but still boxes through `JsValue`. A typed-double specialisation for numeric inner loops would close more of the V8 perf gap, but is L-scope and requires a type-feedback subsystem we don't have. Out of scope for first wave.

---

## 4. CONFORMANCE LONG TAIL — 1,152 runtime-subset failures (Priority: 🟠 high)

**Scope:** ongoing. The runtime subset shows 41.2% pass; full corpus likely lower because the alphabetical first 2,000 over-represents `annexB/*` and `built-ins/A*`.

Top categories from §0 with concrete next-step decomposition:

### 4.1 Annex B legacy / web compatibility (~712 failures in subset)
- `annexB/language/eval-code` (343), `global-code` (112), `function-code` (107): Annex B B.3.3 hoisting semantics for function declarations in blocks, sloppy-mode-specific binding propagation. Read ECMA-262 B.3.3 ("Block-Level Function Declarations Web Legacy Compatibility Semantics") before touching.
- `annexB/built-ins/String` (86): `String.prototype.substr`, `String.prototype.{anchor,big,blink,bold,fixed,fontcolor,fontsize,italics,link,small,strike,sub,sup}` — straightforward, mostly small wrappers. Single commit per ~5 methods.
- `annexB/built-ins/RegExp` (54): `RegExp.prototype.compile`, named capture legacy `$1`–`$9`. Inspect `RegExpBuiltin.cs` first.
- `annexB/built-ins/Date` (21): `Date.prototype.getYear` / `setYear` / `toGMTString` legacy aliases.
- `annexB/built-ins/escape` (16) + `unescape` (19): legacy URI-ish encoder/decoder — small spec, single commit.

### 4.2 ArrayBuffer / TypedArray prototype (~144 failures in subset)
- `built-ins/ArrayBuffer/prototype` (138): mostly `slice`, `transfer`, `transferToFixedLength`, `resize`, detach semantics, species lookup. Each method = separate commit per directives.
- `built-ins/ArrayBuffer/isView` (6), `Symbol.species` (4): trivial — single commit.

### 4.3 Array prototype edge cases (~104 in subset)
Most likely categories: `Symbol.species` cross-realm semantics, `ToObject(this)` corner cases that already shipped for `every`/`copyWithin`/`at`/`fill`/`length`, but other methods need the same fix. Audit `ArrayBuiltin.cs` for remaining methods that bypass `ToObject`.

### 4.4 Array iterator (`ArrayIteratorPrototype/next` — 19 in subset)
ES2015 iterator protocol edge cases. Likely `done`/`value` shape for sparse arrays or detached-buffer typed arrays.

**Approach for §4:** pick one sub-bucket per session, fix and run that exact test262 subdirectory via `--test262-file` or by post-filtering. Move on only when the chosen sub-bucket is clean. Each fix = one commit with the test262 file cited.

---

## 5. TIMEOUT FIXES — 2 (Priority: 🟢 low)

Both in `built-ins/Array/prototype/concat/arg-length-{exceeding,near}-integer-limit.js`. These attempt to construct arrays near `2^32 - 1`. Either:
- Cap the work and throw RangeError early when the resulting `length` would exceed `2^32 - 1` (spec: ECMA-262 23.1.3.2 `Array.prototype.concat`, step 5.b.iii.4 — `Set finalLength to E + n`, then `If finalLength > 2**53 - 1, throw a TypeError`).
- Or short-circuit when the inputs themselves declare `length` past the limit, before the copy loop.

**First commit:** add `length` overflow guard at the top of `ArrayBuiltin.concat` per ECMA-262 23.1.3.2; both timeout tests should pass or fail-fast in <100ms.

---

## 6. PARSER GAPS — 8 runtime-subset tests + 8 parser-subset failures (Priority: 🟢 low)

From `dotnet test` and the runtime-subset failure-message bucket:
- `Invalid function parameter '-->'` / `'<!--'` (3 tests): HTML comment tokens in parameter context — Annex B B.1.3 HTMLLikeComments. Likely a lexer fix.
- `Invalid assignment target.` (4 tests): residual parser-side rejection sites; recent fix `60675408` covered some but not all. Find the 4 specific tests, identify the remaining patterns.

**First commit:** Annex B HTML comments — `FenBrowser.Js/Lexer/JsLexer.cs` already has partial support per commit `8653fa79`; extend to parameter-list positions.

---

## 7. INFRASTRUCTURE / META GAPS (Priority: 🟢 low but cheap)

### 7.1 `.fenjs-progress.md` is stale (S)
Last touched 2026-05-26; misses 20+ commits (IL-JIT steps 1–9, realm isolation, differential testing, tier-6 #29 fixes). Per `fenjs-resume-protocol`, this file is the canonical resume marker. **Update it before any further session.**

### 7.2 `docs/test_results.md` is stale (S)
Shows 27.4% runtime; actual is 41.2%. Update with the fresh numbers from §0 and link this audit file from it.

### 7.3 Test262 baseline JSON regeneration (S)
`FenBrowser.Js.Test262/Baselines/latest-results.json` predates tier-6 #29. Regenerate so CI gates lock in the new floor.

### 7.4 Full-corpus run (M)
The 2,000-test subset is alphabetical and biased. Run the full ~50K corpus once to get a true conformance number and re-prioritise §4. Will take hours — schedule deliberately, not opportunistically.

### 7.5 Differential testing corpus expansion (M)
Tier-6 #30 added a starter corpus of 4 scripts. Useful but tiny. Grow the corpus toward 100+ scripts covering: closures, prototype chain edge cases, generators, async/await interleaving, proxy traps, typed arrays, JSON round-trips, regex backreferences.

---

## 8. SECURITY / ISOLATION GAPS (Priority: 🟡 medium)

`Runtime/JsRealm.cs`, `JsIsolate.cs`, `HostObjectHandle.cs`, `CspPolicy.cs`, `MayExecuteJsAttribute.cs` exist. `BytecodeCache` has a per-thread bypass for isolated realms (tier-5 #28). What still needs verification:

- **Cross-realm leak audit (M):** prove that no JS value created in realm A can be observed in realm B except via the host bridge. Write 10–20 negative tests: shared `globalThis`, leaked prototypes via `Reflect.getPrototypeOf`, shared intrinsics via `Symbol.iterator`, etc.
- **Host object lifetime fuzz (S):** extend `IpcFuzzHarness` or `RuntimeFuzz` to randomly invalidate `HostObjectHandle`s under load and confirm only TypeError surfaces — no crashes, no UAF. Crashes #1/#2/#5 in §1 suggest this code path is fragile.
- **Wall-clock + instruction budget enforcement (verified)** — both already present at `BytecodeInterpreter.cs:601` and `:607`. No gap, but add a unit test that proves both throw on overrun (currently only implicit coverage).

---

## 9. NOT-IN-SCOPE / EXPLICIT DEFERRALS

To keep this list honest, these are known weaknesses **not** addressed by the above:

- **OSR (on-stack replacement)** — L scope, no pending request.
- **Typed numeric IL emit** — L scope, would need type-feedback subsystem.
- **Async iterator / async generator full conformance** — partial coverage today via `ForAwaitAndAsyncGeneratorRuntimeTests`; full ECMA-262 27.5/27.6 audit deferred.
- **`Atomics` / `SharedArrayBuffer`** — not implemented; needs cross-isolate memory model work.
- **`Temporal`** — Stage 3 proposal, not landed.
- **`FinalizationRegistry` GC integration** — currently a no-op per `.fenjs-progress.md`; full integration needs a heap GC pass with finalisation queueing.

---

## Suggested first-wave sequencing (when work resumes)

The directives say one engineering step per commit, smallest-complete unit, verified end-to-end. A reasonable first wave:

1. §7.1 + §7.2 + §7.3 — meta updates. 3 small commits. Unblocks accurate resume state.
2. §1 — 5 crash fixes. 2–4 commits. Production-posture win: crash count → 0.
3. §5 — 2 timeouts. 1 commit. Easy.
4. §6 — Annex B HTML comments + remaining invalid-assignment-target sites. 2 commits.
5. §4.1 sub-buckets (`annexB/built-ins/escape` + `unescape` + Date legacy + String legacy) — 3–4 commits. Cheap conformance points.
6. §2 first slice (Properties extraction) — 1 commit. Begins interpreter breakup.
7. §3.2 — back-edge tier-up counter. 1 commit. Cheap perf win.

After this wave: re-measure all of §0, update this doc with deltas, then choose the next wave (likely §2 slices 2–4 and §4.2 ArrayBuffer prototype).

---

*Audit produced by reading the engine source files directly + fresh test262 + `dotnet test` runs against working-tree commit `0ec6b59f`. All numbers reproducible by re-running the commands cited in §0.*
