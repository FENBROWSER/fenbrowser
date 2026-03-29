# Test262 Conformance Results

**Date**: 2026-03-29 14:02:27
**Duration**: 104787ms (104.8s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 94 |
| Failed | 406 |
| Pass Rate | 18.8% |
| Avg/Test | 209.57ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| call-resolve-element-after-return.js | Test262Error: callCount after call to all() Expected SameValue(«0», «1») to be true |
| call-resolve-element-items.js | Test262Error: callCount after call to all() Expected SameValue(«0», «1») to be true |
| call-resolve-element.js | Test262Error: callCount after call to all() Expected SameValue(«0», «1») to be true |
| capability-executor-called-twice.js | Test262Error: executor initially called with no arguments Expected SameValue(«""», «"abc"») to be true |
| capability-executor-not-callable.js | Test262Error: executor not called at all Expected a TypeError to be thrown but no exception was thrown at all |
| capability-resolve-throws-no-close.js | Test262Error: Expected SameValue(«0», «1») to be true |
| capability-resolve-throws-reject.js | Test262:AsyncTestFailure:Test262Error: Promise incorrectly fulfilled. |
| ctx-ctor-throws.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| ctx-ctor.js | Test262Error: Expected SameValue(«[function]», «[function]») to be true |
| ctx-non-ctor.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| ctx-non-object.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| does-not-invoke-array-setters.js | Async test did not signal $DONE within 10000ms |
| invoke-resolve-error-close.js | Test262Error: Expected SameValue(«0», «1») to be true |
| invoke-resolve-error-reject.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: The promise should not be fulfilled. |
| invoke-resolve-get-error-reject.js | Async test did not signal $DONE within 10000ms |
| invoke-resolve-get-error.js | TypeError: Cannot convert a Symbol value to a string |
| invoke-resolve-get-once-multiple-calls.js | Test262Error: Got `resolve` only once for each iterated value Expected SameValue(«0», «1») to be true |
| invoke-resolve-get-once-no-calls.js | Test262Error: Got `resolve` only once for each iterated value Expected SameValue(«0», «1») to be true |
| invoke-resolve-on-promises-every-iteration-of-custom.js | TypeError: LoadProp 'bind' on undefined |
| invoke-resolve-on-promises-every-iteration-of-promise.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: `then` invoked once for every iterated promise Expe... |
| invoke-resolve-on-values-every-iteration-of-promise.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: `Promise.resolve` invoked once for every iterated v... |
| invoke-resolve-return.js | Test262Error: new `then` method invoked exactly once Expected SameValue(«0», «1») to be true |
| invoke-resolve.js | Test262Error: `resolve` invoked once for each iterated value Expected SameValue(«0», «3») to be true |
| invoke-then-error-close.js | Test262Error: Expected SameValue(«0», «1») to be true |
| invoke-then-error-reject.js | Async test did not signal $DONE within 10000ms |
| invoke-then-get-error-close.js | Test262Error: Expected SameValue(«0», «1») to be true |
| invoke-then-get-error-reject.js | Async test did not signal $DONE within 10000ms |
| invoke-then.js | Test262Error: `then` invoked once for every iterated value Expected SameValue(«0», «3») to be true |
| iter-arg-is-false-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-arg-is-null-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-arg-is-number-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-arg-is-symbol-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-arg-is-true-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-arg-is-undefined-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-false-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-null-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-number-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-string-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-symbol-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-true-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-assigned-undefined-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-next-val-err-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected. |
| iter-returns-false-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-null-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-number-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-string-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-symbol-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-true-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-returns-undefined-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected, but was resolved |
| iter-step-err-reject.js | Test262:AsyncTestFailure:Test262Error: The promise should be rejected. |
| ... | 356 more failures |
