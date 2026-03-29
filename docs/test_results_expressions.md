# Test262 Conformance Results

**Date**: 2026-03-29 14:00:10
**Duration**: 1488ms (1.5s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 417 |
| Failed | 83 |
| Pass Rate | 83.4% |
| Avg/Test | 2.98ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| bigint-arithmetic.js | TypeError: Cannot mix BigInt and other types, use explicit conversions |
| bigint-toprimitive.js | TypeError: @@toPrimitive is not a function |
| coerce-bigint-to-string.js | Test262Error: Expected SameValue(«"NaN"», «"-1"») to be true |
| coerce-symbol-to-prim-invocation.js | TypeError: Cannot convert object to primitive value |
| get-symbol-to-prim-err.js | Test262Error: error from property access of right-hand side Expected a Test262Error but got a TypeError |
| S11.6.1_A2.2_T2.js | Test262Error: #1: var date = new Date(0); date + date === date.toString() + date.toString(). Actual: 0 |
| S11.6.1_A2.2_T3.js | Test262Error: #1: function f1() {return 0;}; f1 + 1 === f1.toString() + 1 |
| S11.6.1_A3.2_T1.2.js | Test262Error: #1: ({} + function(){return 1}) === ({}.toString() + function(){return 1}.toString()). Actual: [object Obj... |
| 11.1.4_4-5-1.js | Test262Error: arr.hasOwnProperty("0") !== true |
| 11.1.4_5-6-1.js | Test262Error: arr.hasOwnProperty("1") !== true |
| spread-err-mult-err-expr-throws.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-mult-err-iter-get-value.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| spread-err-mult-err-itr-get-call.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-mult-err-itr-get-get.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-mult-err-itr-step.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-mult-err-itr-value.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-sngl-err-expr-throws.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-sngl-err-itr-get-call.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-sngl-err-itr-get-get.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-sngl-err-itr-get-value.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| spread-err-sngl-err-itr-step.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-err-sngl-err-itr-value.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| spread-mult-iter.js | Test262Error: Expected SameValue(«4», «5») to be true |
| spread-obj-spread-order.js | Test262Error: Actual [z, a, 1] and expected [1, z, a, Symbol(foo)] should have the same contents.  |
| spread-obj-symbol-property.js | Test262Error: Expected SameValue(«undefined», «1») to be true |
| spread-sngl-iter.js | Test262Error: Expected SameValue(«1», «2») to be true |
| ArrowFunction_restricted-properties.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| dflt-params-ref-later.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| dflt-params-ref-self.js | Test262Error: Expected a ReferenceError to be thrown but no exception was thrown at all |
| dflt-params-trailing-comma.js | no prefix parse function for RParen found
[ParseGroupedExpression] expected next token to be RParen, got Semicolon inste... |
| ary-init-iter-get-err-array-prototype.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| ary-ptrn-elem-id-iter-val-array-prototype.js | TypeError: LoadProp 'length' on undefined |
| dflt-ary-init-iter-get-err-array-prototype.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| dflt-ary-ptrn-elem-id-iter-val-array-prototype.js | TypeError: LoadProp 'length' on undefined |
| eval-var-scope-syntax-err.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| length-dflt.js | Test262Error: descriptor value should be 0; object value should be 0 |
| lexical-new.target-closure-returned.js | Test262Error: Expected SameValue(«2», «1») to be true |
| lexical-new.target.js | Test262Error: Expected SameValue(«0», «1») to be true |
| lexical-super-call-from-within-constructor.js | ReferenceError: super is not defined |
| lexical-super-property-from-within-constructor.js | ReferenceError: super is not defined |
| lexical-super-property.js | TypeError: undefined is not a function |
| lexical-supercall-from-immediately-invoked-arrow.js | ReferenceError: super is not defined |
| param-dflt-yield-expr.js | Parse-negative test parsed successfully |
| params-trailing-comma-multiple.js | no prefix parse function for RParen found
[ParseGroupedExpression] expected next token to be RParen, got Semicolon inste... |
| params-trailing-comma-single.js | no prefix parse function for RParen found
[ParseGroupedExpression] expected next token to be RParen, got Semicolon inste... |
| scope-body-lex-distinct.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| scope-paramsbody-var-open.js | Test262Error: Expected SameValue(«"inside"», «"outside"») to be true |
| arrowparameters-bindingidentifier-rest.js | Parse-negative test parsed successfully |
| arrowparameters-cover-no-duplicates-binding-array-1.js | Parse-negative test parsed successfully |
| arrowparameters-cover-no-duplicates-binding-array-2.js | Parse-negative test parsed successfully |
| ... | 33 more failures |
