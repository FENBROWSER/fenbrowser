# Test262 Conformance Results

**Date**: 2026-03-29 14:00:31
**Duration**: 21206ms (21.2s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 257 |
| Failed | 243 |
| Pass Rate | 51.4% |
| Avg/Test | 42.41ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| Array.from_forwards-length-for-array-likes.js | TypeError: LoadProp 'length' on undefined |
| calling-from-valid-1-noStrict.js | Test262Error: The value of calls[0].thisArg is expected to be this Expected SameValue(«undefined», «[object Object]») to... |
| calling-from-valid-2.js | Test262Error: The value of calls[0].thisArg is expected to equal the value of thisArg Expected SameValue(«undefined», «[... |
| elements-deleted-after.js | TypeError: LoadProp 'arrayIndex' on undefined |
| elements-updated-after.js | Test262Error: The value of value is expected to be 127 Expected SameValue(«4», «127») to be true |
| items-is-null-throws.js | Test262Error: Array.from(null) throws a TypeError exception Expected a TypeError to be thrown but no exception was throw... |
| iter-cstm-ctor-err.js | Test262Error: Array.from.call(C, items) throws a Test262Error exception Expected a Test262Error but got a TypeError |
| iter-cstm-ctor.js | Test262Error: The result of evaluating (result instanceof C) is expected to be true |
| iter-map-fn-err.js | Test262Error: The value of closeCount is expected to be 1 Expected SameValue(«0», «1») to be true |
| iter-map-fn-this-arg.js | Test262Error: The value of thisVals[0] is expected to equal the value of thisVal Expected SameValue(«undefined», «[objec... |
| iter-map-fn-this-non-strict.js | Test262Error: The value of thisVals[0] is expected to equal the value of global Expected SameValue(«undefined», «[object... |
| iter-set-elem-prop-err.js | Test262Error: Array.from.call(constructorSetsIndex0ConfigurableFalse, items) throws a TypeError exception Expected a Typ... |
| iter-set-length-err.js | Test262Error: Array.from.call(poisonedPrototypeLength, items) throws a Test262Error exception Expected a Test262Error to... |
| mapfn-is-not-callable-typeerror.js | Test262Error: Array.from([], null) throws a TypeError exception Expected a TypeError to be thrown but no exception was t... |
| mapfn-is-symbol-throws.js | Test262Error: Array.from([], Symbol("1")) throws a TypeError exception Expected a TypeError to be thrown but no exceptio... |
| proto-from-ctor-realm.js | Test262Error: Object.getPrototypeOf(Array.from.call(C, [])) returns other.Object.prototype Expected SameValue(«», «[obje... |
| source-array-boundary.js | TypeError: LoadProp 'arrayIndex' on undefined |
| source-object-constructor.js | Test262Error: The value of Array.from.call(Object, []).constructor is expected to equal the value of Object Expected Sam... |
| source-object-iterator-1.js | Test262Error: Array.from(obj) throws a Test262Error exception Expected a Test262Error to be thrown but no exception was ... |
| source-object-length-set-elem-prop-err.js | Test262Error: Array.from.call(A1, items) throws a TypeError exception Expected a TypeError to be thrown but no exception... |
| async-iterable-async-mapped-awaits-once.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Expected SameValue(«0», «3») to be true |
| async-iterable-input-does-not-await-input.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [[object Promise]] should have the same cont... |
| async-iterable-input.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [0,1,2] and expected [0, 1, 2] should have the same contents... |
| asyncitems-array-add-to-singleton.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [1] and expected [1, 7] should have the same contents.  |
| asyncitems-array-add.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [1, 2, 3] and expected [1, 2, 3, 4] should have the same con... |
| asyncitems-array-mutate.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [1, 2, 3] and expected [1, 8, 3] should have the same conten... |
| asyncitems-array-remove.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [1, 2, 3] and expected [1, 2] should have the same contents.... |
| asyncitems-arraylike-length-accessor-throws.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: Promise should be rejected if array-like length can... |
| asyncitems-arraylike-promise.js | no prefix parse function for RBrace found |
| asyncitems-arraylike-too-long.js | Test timed out after 10000ms |
| asyncitems-asynciterator-exists.js | no prefix parse function for RBrace found |
| asyncitems-asynciterator-null.js | no prefix parse function for RBrace found |
| asyncitems-asynciterator-sync.js | no prefix parse function for RBrace found |
| asyncitems-asynciterator-throws.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: Expected a Test262Error to be thrown asynchronously... |
| asyncitems-bigint.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [1, 2] should have the same contents.  |
| asyncitems-boolean.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [1, 2] should have the same contents.  |
| asyncitems-function.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [1, 2] should have the same contents.  |
| asyncitems-iterator-exists.js | no prefix parse function for RBrace found |
| asyncitems-iterator-null.js | no prefix parse function for RBrace found |
| asyncitems-iterator-promise.js | no prefix parse function for RBrace found |
| asyncitems-iterator-throws.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: Expected a Test262Error to be thrown asynchronously... |
| asyncitems-number.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [1, 2] should have the same contents.  |
| asyncitems-operations.js | no prefix parse function for RBrace found |
| asyncitems-symbol.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Actual [] and expected [1, 2] should have the same contents.  |
| mapfn-async-throws-close-async-iterator.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: async mapfn rejecting should cause fromAsync to rej... |
| mapfn-async-throws-close-sync-iterator.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: async mapfn rejecting should cause fromAsync to rej... |
| mapfn-result-awaited-once-per-iteration.js | no prefix parse function for RBrace found |
| mapfn-sync-throws-close-async-iterator.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: sync mapfn throwing should cause fromAsync to rejec... |
| mapfn-sync-throws-close-sync-iterator.js | Test262:AsyncTestFailure:Test262Error: Uncaught JS Exception: Error: sync mapfn throwing should cause fromAsync to rejec... |
| non-iterable-input-with-thenable-async-mapped-awaits-callback-result-once.js | Test262:AsyncTestFailure:Test262Error: Test262Error: Expected SameValue(«0», «3») to be true |
| ... | 193 more failures |
