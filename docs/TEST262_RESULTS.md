# Test262 Suite Results - Language Category

**Date**: 2026-02-09
**Suite**: `test262/test/language`
**Total Tests**: 23,614
**Pass Rate**: 54.12%

## Summary

| Metric     | Count  | Percentage |
| :--------- | :----- | :--------- |
| **Total**  | 23,614 | 100%       |
| **Passed** | 12,779 | 54.12%     |
| **Failed** | 10,835 | 45.88%     |

## Harness Infrastructure Update (2026-02-12)

FenBrowser now maintains a **generated NUnit Test262 suite** in `FenBrowser.Test262` using `Test262Harness` (same style used by engines like Jint integration examples), in addition to the existing in-engine runner.

- Generation command is scripted via `FenBrowser.Test262/generate_test262.ps1`.
- Generated fixtures live under `FenBrowser.Test262/Generated`.
- FenRuntime execution is provided by `FenBrowser.Test262/Test262RuntimeAdapter.cs`.
- Negative-test handling in generated base fixture was hardened so expected throws are enforced correctly.

## Failure Analysis

Based on the verification run, frequent failure modes include:

### 1. Permissive Syntax (Missing Early Errors)

The engine accepts syntax that should be illegal.

- **Example**: `ary-ptrn-rest-not-final-ary.js`
- **Code**: `([..., y]) => {}` (Rest element must be last)
- **Expected**: `SyntaxError`
- **Actual**: Success (No Error)
- **Cause**: Parser does not enforce "Rest element must be last element" validation in destructuring patterns.

### 2. Unicode Whitespace Handling

The lexer fails to recognize some Unicode whitespace characters as valid separators.

- **Example**: `after-regular-expression-literal-nbsp.js` (U+00A0 NBSP)
- **Error**: `no prefix parse function for Illegal found`
- **Cause**: The Lexer likely only handles ASCII whitespace (space, tab, newline) and misses `\u00A0`, `\u2028`, etc.

### 3. Strict Mode Compliance

Many tests rely on strict mode parsing constraints which are not fully enforced.

## Conclusion

The engine has a strong baseline (54% on the hardest suite) but requires significant work on:

- **Syntax Validation**: Rejecting invalid code (destructuring rules, duplicate args, etc.).
- **Unicode Support**: Updating Lexer to handle full Unicode whitespace set.
- **Edge Case Handling**: Regex parsing and literals.

## Incremental Fixes (2026-02-10)

Validated parser/harness regressions addressed against representative Test262 files:

- **Dynamic import parse fix**: `import("./x.js")` no longer triggers a spurious extra-`)` parse error (`always-create-new-promise.js` now passes).
- **Module export star parsing fix**: `export * from "...";` and `export * as default from "...";` parse correctly (`namespace-unambiguous-if-export-star-as-from.js`, `export-star-as-dflt.js` now pass).
- **Import specifier IdentifierName support**: `import { default as named } from "...";` no longer fails due keyword-token rejection in import specifiers.
- **Escaped identifier lexing fix**: corrected `\uHHHH` identifier decoding cursor advance to avoid trailing hex digit contamination.
- **Test262 flags ingestion**: runner now parses `flags: [onlyStrict|noStrict|async]` and applies `onlyStrict` prelude strictness setup.

## Incremental Fixes (2026-02-10, Harness-Only Pass)

Validated with `dotnet run --project FenBrowser.FenEngine -- test262-range ...`:

- `40000-40199`: **37 fails -> 13 fails**.
- `45000-45199`: **47 fails -> 12 fails**.
- `33000-33199`: **18 fails -> 0 fails**.

Parser/Lexer hardening applied:

- **Unicode identifier expansion** in `Core/Lexer.cs`:
  - Added broader `IdentifierStart`/`IdentifierPart` checks.
  - Added `Other_ID_Start`/`Other_ID_Continue` handling and surrogate tolerance for astral code points.
  - Updated private identifier and default identifier tokenization to use the widened checks.
- **Strict/global early-error checks** in `Core/Parser.cs`:
  - Script-goal import/export declarations now report syntax errors.
  - Top-level `return` now reports `Illegal return statement`.
  - `new.target` now reports syntax error when not in function context.
  - `super` usage outside class context reports syntax error.
  - Private identifiers outside class context report syntax error.
- **Yield/await context hardening**:
  - Added async/generator nesting context tracking.
  - Enforced `await` validity in async function contexts and non-empty await argument.
  - Enforced `yield`/`await` binding identifier restrictions in generator/async contexts.
  - Tightened `yield` terminator handling (notably `]`, `)`, `,`, `:` cases) to fix async-generator spread edge parsing.
- **Binding pattern validation**:
  - Enforced rest element placement and initializer bans in array/object binding patterns.
  - Improved parameter parsing acceptance for `IdentifierName` plus explicit syntax errors on unexpected parameter tokens.

## Incremental Fixes (2026-02-10, Class-Element Early-Error Sweep)

Validated with `dotnet run --project FenBrowser.FenEngine -- test262-range ...`:

- `34000-34199`: **28 fails -> 0 fails**
- `34000-34999`: **245 fails -> 19 fails**
- `40000-40199`: **1 fail** (unchanged single runtime-semantics case)
- `45000-45199`: **0 fails**
- `41000-41999`: **608 fails** (current dominant cluster: regex/string literal validation + module early errors)

Parser/runner hardening applied:

- **Parse-negative normalization** (`Testing/Test262Runner.cs`):
  - Parse-phase negatives expecting `SyntaxError` now pass on any parser-reported parse error, avoiding false negatives from non-spec-text diagnostic wording.
- **Class strict-mode + private-name scope tracking** (`Core/Parser.cs`):
  - Class bodies force strict parse context.
  - Nested class parsing now inherits enclosing private-name visibility.
- **Class early-error enforcement** (`Core/Parser.cs`):
  - Duplicate constructor rejection.
  - Duplicate private-name rejection (except one getter + one setter pair).
  - `#constructor` rejection for private elements.
  - Static method/accessor `prototype` name rejection.
  - `super()` rejection outside constructors and in constructors without heritage.
  - `super.#name` rejection.
  - `delete` on private references rejection.
- **Class field initializer early-errors** (`Core/Parser.cs`):
  - Rejects initializer trees containing `super()` or `arguments` (including nested arrow/function forms).
- **Method/accessor parameter early-errors** (`Core/Parser.cs`):
  - Duplicate parameters, trailing comma after rest, and default on rest now flagged.
  - `ContainsUseStrict` + non-simple parameter list rejection.
  - `yield` (and async `await`) in invalid method parameter positions rejected.
- **Class heritage expression parsing** (`Core/Parser.cs`):
  - `extends` now parses general expressions, fixing valid parenthesized/arrow heritage expression parse paths.

## Smoke Check (2026-03-05): First 100 Test262 Cases

Execution command (runnable harness):
- `dotnet run --project FenBrowser.Test262/FenBrowser.Test262.csproj -c Release -- run_chunk 1 --chunk-size 100 --format json --output Results/test262_chunk1_100.json`

Notes:
- `FenBrowser.FenEngine` itself is currently `OutputType=Library`, so direct `dotnet run --project FenBrowser.FenEngine -- test262-range ...` is not runnable.
- The dedicated `FenBrowser.Test262` CLI is the valid executable harness.

Result:
- Total: **100**
- Passed: **47**
- Failed: **53**
- Pass rate: **47.0%**
- Duration: **1540ms**
- Output artifact: `Results/test262_chunk1_100.json`

Initial failure clusters observed from this slice:
- Date semantics mismatches (`this-time-nan`, `time-clip`, `year-*` style cases)
- RegExp legacy accessor semantics (`RegExp.$1`, `input`, `lastMatch`, `leftContext`, `rightContext`)
- TypeError vs expected-error-class mismatches in assertion helpers

## Targeted Proxy Regression Fix (2026-03-05)

Validation command:
- `dotnet run --project FenBrowser.Test262/FenBrowser.Test262.csproj -c Release -- run_single staging/sm/Proxy/global-receiver.js --isolate-process --verbose`

Result:
- `staging/sm/Proxy/global-receiver.js`: **PASS**

Fix summary:
- Preserved original receiver in window-named fallback prototype `document` lookups (`FenObject.TryResolveWindowNamedProperty(...)`).
- Kept non-strict undeclared assignment path routed to global object semantics in VM update flow.
- Eliminated prior crash mode where receiver-assert mismatch cascaded into stack overflow while formatting assertion output.

## Object.prototype Recheck (2026-03-06)

- Scope: `built-ins/Object/prototype`
- Baseline artifact: `Results/object_prototype_recheck_20260306_002757.json`
  - Passed: `151`
  - Failed: `97`
- Post-fix artifact: `Results/object_prototype_recheck_after_fix_20260306_003347.json`
  - Passed: `165`
  - Failed: `83`
- Delta: **+14 passed / -14 failed**

Focused Annex-B status:
- `built-ins/Object/prototype/__defineGetter__`: `9/11` pass (remaining `define-abrupt.js`, `key-invalid.js`)
- `built-ins/Object/prototype/__defineSetter__`: `9/11` pass (remaining `define-abrupt.js`, `key-invalid.js`)

## Object.prototype Recheck Update (2026-03-06, Annex-B + Proxy defineProperty abrupt completions)

- Prior checkpoint artifact: `Results/object_prototype_recheck_after_fix_20260306_003347.json`
  - Passed: `165`
  - Failed: `83`
- New checkpoint artifact: `Results/object_prototype_recheck_after_annexb_proxyfix_20260306_004154.json`
  - Passed: `172`
  - Failed: `76`
- Delta from prior checkpoint: **+7 passed / -7 failed**
- Total delta from first 2026-03-06 baseline (`151/248`): **+21 passed / -21 failed**

Focused status:
- `built-ins/Object/prototype/__defineGetter__`: `11/11` pass
- `built-ins/Object/prototype/__defineSetter__`: `11/11` pass

## Object.prototype Recheck Update (2026-03-06, lookup + __proto__ completion)

- Prior checkpoint artifact: `Results/object_prototype_recheck_after_annexb_proxyfix_20260306_004154.json`
  - Passed: `172`
  - Failed: `76`
- New checkpoint artifact: `Results/object_prototype_recheck_after_protofix_20260306_010715.json`
  - Passed: `194`
  - Failed: `54`
- Delta from prior checkpoint: **+22 passed / -22 failed**
- Total delta from first 2026-03-06 baseline (`151/248`): **+43 passed / -43 failed**

Focused status:
- `built-ins/Object/prototype/__lookupGetter__`: `16/16` pass
- `built-ins/Object/prototype/__lookupSetter__`: `16/16` pass
- `built-ins/Object/prototype/__proto__`: `15/15` pass

## Destructuring / Loop-Head Hardening (2026-03-06)

- Baseline chunk 50 artifact: `Results/test262_full_run_10workers_20260305_185350/workers/worker_10/analysis/chunk_050_failed.md`
  - Passed: `569`
  - Failed: `431`
- First checkpoint artifact: `Results/tranche1_chunk50_20260306.json`
  - Passed: `576`
  - Failed: `424`
- Second checkpoint artifact: `Results/tranche1_chunk50_20260306_rerun.json`
  - Passed: `617`
  - Failed: `383`
- Third checkpoint artifact: `Results/tranche1_chunk50_20260306_rerun2.json`
  - Passed: `618`
  - Failed: `382`
- Net delta from original chunk-50 baseline: **+49 passed / -49 failed**

Focused confirmations:
- `run_single language/statements/for-of/head-lhs-member.js` -> pass (`Results/forof_head_lhs_member_20260306.json`)
- `run_single language/statements/for-in/head-lhs-member.js` -> pass (`Results/forin_head_lhs_member_20260306.json`)
- Remaining parser early-error gap still open: `run_single language/statements/for-of/head-lhs-non-asnmt-trgt.js` still fails parse-negative enforcement (`Results/forof_head_lhs_non_asnmt_20260306.json`).

## Destructuring Iterator/Name Follow-Up (2026-03-06)

- Fourth checkpoint artifact: Results/tranche1_chunk50_20260306_rerun3.json
  - Passed: 727
  - Failed: 273
- Delta from third checkpoint: **+109 passed / -109 failed**
- Total delta from original chunk-50 baseline: **+158 passed / -158 failed**

Focused confirmations:
- un_single language/statements/for-of/head-lhs-non-asnmt-trgt.js -> pass (Results/forof_head_lhs_non_asnmt_20260306_c.json)
- un_single language/statements/for-in/head-lhs-non-asnmt-trgt.js -> pass (Results/forin_head_lhs_non_asnmt_20260306_b.json)
- un_single language/statements/variable/dstr/ary-init-iter-close.js -> pass (Results/dstr_iter_close_20260306.json)
- un_single language/statements/variable/dstr/ary-ptrn-elem-id-init-fn-name-arrow.js -> pass (Results/dstr_fn_name_arrow_var_20260306.json)
- un_single language/statements/variable/dstr/obj-ptrn-id-init-fn-name-fn.js -> pass (Results/dstr_fn_name_fn_obj_20260306.json)
- Remaining targeted gap: language/statements/variable/dstr/ary-ptrn-elem-id-init-fn-name-class.js still fails around static 
ame handling (Results/dstr_fn_name_class_var_20260306_b.json).

## Class Static name Cleanup (2026-03-06)

- Follow-up artifact: Results/tranche1_chunk50_20260306_rerun4.json
  - Passed: 727
  - Failed: 273
- Aggregate status unchanged from rerun3, but the remaining targeted anonymous-class destructuring 
ame regression is closed.

Focused confirmations:
- un_single language/statements/variable/dstr/ary-ptrn-elem-id-init-fn-name-class.js -> pass (Results/dstr_fn_name_class_var_20260306_c.json)
- un_single language/statements/variable/dstr/ary-ptrn-elem-id-init-fn-name-arrow.js -> pass (Results/dstr_fn_name_arrow_var_20260306_b.json)

## Loop Parser / Hoist Follow-Up (2026-03-06)

- First tranche-2 parser rerun artifact: `Results/tranche2_chunk50_20260306_parser_rerun.json`
  - Passed: `741`
  - Failed: `259`
- Second tranche-2 parser rerun artifact: `Results/tranche2_chunk50_20260306_parser_rerun2.json`
  - Passed: `746`
  - Failed: `254`
- Delta from tranche-1 rerun4 (`727/1000`): **+19 passed / -19 failed**
- Delta from original chunk-50 baseline (`569/1000`): **+177 passed / -177 failed**

Focused confirmations:
- `run_single language/statements/for-of/head-var-no-expr.js` -> pass (`Results/forof_head_var_no_expr_20260306.json`)
- `run_single language/statements/for-of/decl-let.js` -> pass (`Results/forof_decl_let_20260306.json`)
- `run_single language/statements/for/labelled-fn-stmt-let.js` -> pass (`Results/for_labelled_fn_stmt_let_20260306.json`)
- `run_single language/statements/for-in/labelled-fn-stmt-lhs.js` -> pass (`Results/forin_labelled_fn_stmt_lhs_20260306.json`)
- `run_single language/statements/for-of/let-array-with-newline.js` -> pass (`Results/forof_let_array_newline_20260306.json`)
- `run_single language/statements/do-while/decl-async-fun.js` -> pass (`Results/dowhile_decl_async_fun_20260306.json`)
- `run_single language/statements/for/head-var-bound-names-in-stmt.js` -> pass (`Results/for_head_var_bound_names_20260306.json`)
- `run_single language/statements/for-in/head-var-bound-names-in-stmt.js` -> pass (`Results/forin_head_var_bound_names_20260306.json`)
- `run_single language/statements/for-of/head-var-bound-names-in-stmt.js` -> pass (`Results/forof_head_var_bound_names_20260306.json`)
- `run_single language/statements/for-of/head-const-bound-names-in-stmt.js` -> pass (`Results/forof_head_const_bound_names_20260306.json`)
- `run_single language/statements/for-of/head-let-bound-names-in-stmt.js` -> pass (`Results/forof_head_let_bound_names_20260306.json`)
- `run_single language/statements/for/head-const-bound-names-in-stmt.js` -> pass (`Results/for_head_const_bound_names_20260306.json`)
- `run_single language/statements/for/head-let-bound-names-in-stmt.js` -> pass (`Results/for_head_let_bound_names_20260306.json`)

Open gaps after rerun:
- Per-iteration lexical scope/runtime tests still fail (`scope-body-lex-*`, `scope-head-lex-*`).
- Several remaining parse/early-error misses are strict-mode parameter/body validation, not loop parsing.
- Proposal-era `using` / explicit-resource-management tests still fail at parse time and remain out of the current core fix path.

### Newline `let` ASI Cleanup (2026-03-06)

- Final tranche-2 parser rerun artifact: `Results/tranche2_chunk50_20260306_parser_rerun3.json`
  - Passed: `750`
  - Failed: `250`
- Delta from tranche-2 rerun2 (`746/1000`): **+4 passed / -4 failed**

Focused confirmations:
- `run_single language/statements/for-of/let-block-with-newline.js` -> pass (`Results/forof_let_block_newline_20260306.json`)
- `run_single language/statements/for-of/let-identifier-with-newline.js` -> pass (`Results/forof_let_identifier_newline_20260306.json`)
- `run_single language/statements/for-of/let-array-with-newline.js` -> still correctly parse-negative (`Results/forof_let_array_newline_20260306_b.json`)

## Lexical Loop Runtime Follow-Up (2026-03-06)

- Chunk 50 lexical-runtime rerun artifact: `Results/tranche2_chunk50_20260306_lexical_runtime_rerun.json`
  - Passed: `760`
  - Failed: `240`
- Delta from tranche-2 parser rerun3 (`750/1000`): **+10 passed / -10 failed**
- Delta from original chunk-50 baseline (`569/1000`): **+191 passed / -191 failed**

Focused confirmations:
- `run_single language/statements/for-of/scope-head-lex-open.js` -> pass
- `run_single language/statements/for-of/scope-head-lex-close.js` -> pass
- `run_single language/statements/for-of/scope-body-lex-open.js` -> pass
- `run_single language/statements/for-of/scope-body-lex-close.js` -> pass
- `run_single language/statements/for-of/scope-body-lex-boundary.js` -> pass
- `run_single language/statements/for-of/scope-body-var-none.js` -> pass

What changed:
- Lexical `for-in/of` declarations now create a TDZ scope for RHS evaluation and a fresh lexical scope for each iteration.
- `break` and `continue` now emit loop-scope cleanup for lexical `for-in/of` iterations so the per-iteration environment is not left active on control-flow exits.
- `typeof` on TDZ bindings now throws `ReferenceError` instead of treating the TDZ sentinel as a normal value.
- `var` declarations without initializers now create bindings via a non-clobbering declaration path instead of being dropped entirely at runtime.
- `for-of` RHS parsing now rejects unparenthesized comma expressions through precedence handling while still allowing valid parenthesized comma expressions such as `(probeExpr = ..., [])`.
- Additional targeted confirmations after the chunk rerun:
  - `run_single language/statements/for-in/scope-head-lex-open.js` -> pass
  - `run_single language/statements/for-in/scope-head-lex-close.js` -> pass
  - `run_single language/statements/for-in/scope-body-lex-open.js` -> pass
  - `run_single language/statements/for-in/scope-body-lex-close.js` -> pass
  - `run_single language/statements/for-in/scope-body-lex-boundary.js` -> pass
  - `run_single language/statements/for-in/scope-body-var-none.js` -> pass
