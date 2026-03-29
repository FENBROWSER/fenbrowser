# Test262 Conformance Results

**Date**: 2026-03-29 13:59:58
**Duration**: 2122ms (2.1s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 175 |
| Failed | 325 |
| Pass Rate | 35.0% |
| Avg/Test | 4.24ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| declaration-returns-promise.js | Test262Error: async functions return promise instances |
| dflt-params-ref-later.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: function should not be resolved |
| dflt-params-ref-self.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: function should not be resolved |
| dflt-params-trailing-comma.js | Test262Error: length is properly set Expected SameValue(«2», «1») to be true |
| eval-var-scope-syntax-err.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: function should not be resolved |
| evaluation-mapped-arguments.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Expected SameValue(«1», «2») to be true |
| let-newline-await-in-async-function.js | Parse-negative test parsed successfully |
| syntax-declaration-no-line-terminator.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| try-return-finally-reject.js | Test262:AsyncTestFailure:Test262Error: early-return |
| try-return-finally-return.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: Return in finally block Expected SameValue(«"early-... |
| try-return-finally-throw.js | Test262:AsyncTestFailure:Test262Error: early-return |
| unscopables-with-in-nested-fn.js | Test262:AsyncTestFailure:Test262Error: Test262Error: The value of `v` is 10 Expected SameValue(«undefined», «10») to be ... |
| unscopables-with.js | Test262:AsyncTestFailure:Test262Error: Test262Error: The value of `v` is 10 Expected SameValue(«undefined», «10») to be ... |
| dflt-params-abrupt.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| dflt-params-arg-val-not-undefined.js | TypeError: undefined is not a function |
| dflt-params-arg-val-undefined.js | TypeError: undefined is not a function |
| dflt-params-ref-later.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| dflt-params-ref-prior.js | TypeError: undefined is not a function |
| dflt-params-ref-self.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| dflt-params-trailing-comma.js | TypeError: undefined is not a function |
| ary-init-iter-close.js | TypeError: undefined is not a function |
| ary-init-iter-get-err-array-prototype.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| ary-init-iter-get-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| ary-init-iter-no-close.js | TypeError: undefined is not a function |
| ary-name-iter-val.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-elem-init.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-elem-iter.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-elision-init.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-elision-iter.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-empty-init.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-empty-iter.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-rest-init.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-rest-iter.js | TypeError: undefined is not a function |
| ary-ptrn-elem-ary-val-null.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| ary-ptrn-elem-id-init-exhausted.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-fn-name-arrow.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-fn-name-class.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-fn-name-cover.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-fn-name-fn.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-fn-name-gen.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-hole.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-skipped.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-throws.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| ary-ptrn-elem-id-init-undef.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-init-unresolvable.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| ary-ptrn-elem-id-iter-complete.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-iter-done.js | TypeError: undefined is not a function |
| ary-ptrn-elem-id-iter-step-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| ary-ptrn-elem-id-iter-val-array-prototype.js | TypeError: LoadProp 'length' on undefined |
| ary-ptrn-elem-id-iter-val-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| ... | 275 more failures |
