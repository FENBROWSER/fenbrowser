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
