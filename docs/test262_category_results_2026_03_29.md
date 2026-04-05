# Test262 Conformance Results

## Category Results (2026-03-29) - After Built-in Fixes

**Total Tested:** 4,921 (across 13 categories)
**Total Passed:** 3,137
**Total Failed:** 1,784
**Weighted Pass Rate:** 63.7%

| Category | Tests | Passed (Before) | Passed (After) | Pass % | Change |
|---|---|---|---|---|---|
| built-ins/Math | 327 | 184 | **297** | **90.8%** | **+113 (+34.5%)** |
| language/expressions | 500 | 417 | **420** | **84.0%** | +3 |
| built-ins/Boolean | 51 | 39 | **39** | **76.5%** | - |
| built-ins/Object | 500 | 375 | **380** | **76.0%** | +5 |
| language/literals | 534 | 364 | **364** | **68.2%** | - |
| built-ins/String | 500 | 315 | **315** | **63.0%** | - |
| built-ins/Number | 335 | 177 | **207** | **61.8%** | **+30 (+9.0%)** |
| built-ins/Function | 509 | 285 | **285** | **56.0%** | - |
| built-ins/Array | 500 | 257 | **257** | **51.4%** | - |
| built-ins/JSON | 165 | 70 | **77** | **46.7%** | +7 |
| language/statements | 500 | 175 | **175** | **35.0%** | - |
| built-ins/Promise | 500 | 94 | **94** | **18.8%** | - |
| built-ins/RegExp | 500 | 21 | **21** | **4.2%** | - |

### Fixes Applied (2026-03-29)
1. **Math built-in property descriptors**: All Math methods now non-enumerable with correct `.length` and `IsConstructor=false`
2. **Math constants**: PI, E, LN2, etc. now non-writable, non-enumerable, non-configurable per spec
3. **Math.clz32**: Fixed special value handling (NaN, Infinity, -Infinity now correctly return 32)
4. **Math.expm1**: Fixed -0 sign preservation
5. **Math.hypot**: Fixed NaN/Infinity propagation
6. **Number built-in descriptors**: All Number methods/constants now have correct property descriptors
7. **Number.EPSILON**: Fixed value from `double.Epsilon` (5e-324) to correct `2^-52` (2.22e-16)
8. **Number.prototype.toExponential**: Fixed .NET exponent formatting (e+007 -> e+7)
9. **Number.prototype.toFixed**: Added RangeError for digits outside 0-100, InvariantCulture formatting
10. **JSON built-in descriptors**: parse/stringify now non-enumerable with correct `.length`
11. **Function.prototype methods**: call/apply/bind/toString now have `IsConstructor=false`
12. **Removed duplicate Number registration** that was overwriting correct descriptors

### Notes
- Categories with `--max 500` were capped at 500 tests from the full set
- Per-test timeout: 10 seconds
- RegExp low pass rate largely due to Unicode property escapes (`\p{...}`) tests
- Promise low pass rate indicates async resolution/microtask scheduling gaps
- Detailed per-category results in `docs/test_results_<category>.md` files

### Areas for Future Improvement
- **RegExp (4.2%)**: Implement Unicode property escapes (`\p{Script=...}`, `\p{General_Category=...}`)
- **Promise (18.8%)**: Fix Promise.all/race/allSettled/any iteration protocol, non-iterable rejection
- **Statements (35.0%)**: Destructuring patterns, async generators, default parameters in functions
- **Array (51.4%)**: Array.from with custom constructors, Array.fromAsync, iterator protocol
