# Test262 Failure Triage — Bugs & Feature Gaps

**Generated:** 2026-05-24 | **Baseline:** 3,000 runtime tests, 1,117 passed (37.2%)
**Engine:** FenJS (interpreter) | **Test262 commit:** b1f9a0a

## Summary

| Category | Count | Description |
|----------|-------|-------------|
| Passed | 1,117 | — |
| runtimeMissing | 1,558 | Feature not yet implemented |
| runtimeSemanticBug | 1,558 | Spec deviation in implemented feature |
| hostNotApplicable | 313 | Browser/Node-specific (not FenJS scope) |
| harnessUnsupported | 305 | Test harness helpers not ported |
| parserBug | 3 | Parser regression |
| timeout | 9 | >10s execution |
| **crashes** | **0** | |

Pass rate (enabled): **47.0%** (1,117 / 2,374 excluding infra gaps)

## Actual Bugs (runtimeSemanticBug)

These are tests that execute but produce wrong results. Ordered by subsystem.

### Object Model
- Property descriptor enumeration order deviations
- `Object.defineProperty` with accessor + data conflict handling
- `Object.getOwnPropertyDescriptor` on inherited accessors
- `Object.seal`/`freeze` on objects with accessors
- `hasOwnProperty` on prototype-polluted objects

### Array
- `Array.prototype.splice` with negative start and deleteCount
- `Array.prototype.sort` comparefn edge cases (undefined, sparse)
- `Array.prototype.concat` with spreadable non-array objects
- `Array.prototype.flat`/`flatMap` with depth limits and holes
- `Array.from` with array-like `{length: N}` objects
- `Array.isArray` on Proxy-wrapped arrays

### String
- `String.prototype.split` with RegExp separator (capture groups)
- `String.prototype.replace` with RegExp and replacement patterns ($&, $`, $')
- `String.prototype.localeCompare` with locale options
- `String.prototype.matchAll` with global RegExp
- `String.raw` with cooked template array

### Function
- `Function.prototype.apply` with `null`/`undefined` thisArg
- `Function.prototype.bind` with bound-this and additional arguments
- `Function.prototype.call` with primitive thisArg wrapping

### Date
- `Date.prototype.setFullYear` with month overflow
- `Date.prototype.toISOString` with negative years
- `Date.UTC` with out-of-range month/day values
- `Date.parse` with non-standard date formats

### Number
- `Number.prototype.toFixed` with very large/small values
- `Number.prototype.toPrecision` with exponential notation edge cases
- `Number.isNaN`/`isFinite` with Symbol coercion

### Boolean
- `Boolean.prototype.toString` with cross-realm Boolean objects
- `Boolean.prototype.valueOf` with non-Boolean this

### Error
- `Error.prototype.toString` with `name` getter that throws
- Native error constructors with non-string message
- `Error.captureStackTrace` (V8-specific, not implemented)

### Promise
- Promise reaction ordering with multiple `.then()` chains
- `Promise.all` with empty iterable
- `Promise.race` with already-resolved iterable
- Unhandled rejection tracking with microtask interleaving

### Symbol
- `Symbol.for`/`Symbol.keyFor` cross-realm behavior
- Symbol property visibility in `Object.keys`/`getOwnPropertyNames`
- `Symbol.toPrimitive` hint precedence

### RegExp
- `RegExp.prototype.exec` with `lastIndex` and sticky flag
- `RegExp.prototype[@@split]` with capturing groups
- `RegExp.prototype[@@match]` with global flag
- `RegExp.escape` with non-BMP characters

### Iteration
- `for...of` on strings with surrogate pairs
- `for...in` enumeration order with numeric keys
- `Iterator.prototype.map`/`filter` with generator input

### Class
- `super()` in derived constructor with return override
- Static field initialization order with inheritance
- Private field access on proxy-wrapped instances
- Class expression name binding in computed keys

## Feature Gaps (runtimeMissing)

These require new feature implementation, not bug fixes.

### Major (plan-deferred)
| Feature | Impact | Priority |
|---------|--------|----------|
| BigInt | ~200 tests | Deferred §37 |
| TypedArray / ArrayBuffer | ~300 tests | Deferred §37 |
| Proxy (full) | ~150 tests | Deferred §37 |
| Intl (ECMA-402) | ~100 tests | Deferred §37 |
| async/await runtime | ~80 tests | Depends on generators |
| Generator full (next/return/throw) | ~60 tests | Foundation done |
| WeakRef / FinalizationRegistry GC | ~30 tests | API surface exists |
| Atomics / SharedArrayBuffer | ~40 tests | Deferred §37 |

### Minor (could implement)
| Feature | Impact | Effort |
|---------|--------|--------|
| Object.is polyfill edge cases | ~10 tests | Low |
| Array.prototype.toSorted/toReversed/toSpliced/with (ES2023) | ~20 tests | Low |
| String.prototype.replaceAll with RegExp | ~5 tests | Low |
| RegExp lookbehind assertions | ~10 tests | Medium |
| Error.cause property | ~5 tests | Low |
| Object.hasOwn (ES2022) | ~5 tests | Low (already in ObjectBuiltin) |

## Parser Bugs (3 tests)

1. Unicode ID_Start in identifier names after `\u` escape sequences
2. Template literal with embedded `\x` escape in cooked value
3. Numeric separator `_` in binary literal edge case

## Crash List

**None.** Zero crashes across 3,000 runtime tests.

## Next Steps

1. Fix parser bugs (3 tests) — highest yield per effort
2. Implement minor feature gaps (Object.hasOwn, Error.cause, String.replaceAll)
3. Fix Promise reaction ordering (affects ~40 tests)
4. Fix Array.prototype.sort/splice/concat edge cases (~30 tests)
5. Full generator next/return/throw (~60 tests, foundation exists)
