# Test262 Conformance Results

**Date**: 2026-03-29 13:59:57
**Duration**: 2428ms (2.4s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 165 |
| Passed | 70 |
| Failed | 95 |
| Pass Rate | 42.4% |
| Avg/Test | 14.72ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| 15.12-0-4.js | Test262Error: i Expected SameValue(«2», «0») to be true |
| basic.js | TypeError: undefined is not a function |
| builtin.js | Test262Error: JSON.isRawJSON is extensible |
| length.js | TypeError: Cannot convert undefined or null to object |
| name.js | TypeError: Cannot convert undefined or null to object |
| not-a-constructor.js | Test262Error: isConstructor invoked with a non-function value |
| prop-desc.js | Test262Error: obj should have an own property isRawJSON |
| 15.12.2-2-6.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| duplicate-proto.js | Test262Error: Expected SameValue(«[object Object]», «2») to be true |
| length.js | Test262Error: descriptor value should be 2; object value should be 2 |
| not-a-constructor.js | Test262Error: isConstructor(JSON.parse) must return false Expected SameValue(«true», «false») to be true |
| prop-desc.js | Test262Error: descriptor should not be enumerable |
| revived-proxy-revoked.js | Test262Error: Expected a TypeError but got a SyntaxError |
| revived-proxy.js | SyntaxError: JSON.parse: Uncaught JS Exception: TypeError: StoreProp on undefined |
| reviver-array-define-prop-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| reviver-array-delete-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-array-get-prop-from-prototype.js | Test262Error: Expected SameValue(«2», «3») to be true |
| reviver-array-length-coerce-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-array-length-get-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-call-args-after-forward-modification.js | SyntaxError: JSON.parse: Uncaught JS Exception: TypeError: Cannot destructure undefined |
| reviver-call-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| reviver-call-order.js | Test262Error: Actual [] and expected [1, 2, p1, p2, ] should have the same contents.  |
| reviver-context-source-array-literal.js | SyntaxError: JSON.parse: Uncaught JS Exception: Error: context should be an object Expected SameValue(«"undefined"», «"o... |
| reviver-context-source-object-literal.js | SyntaxError: JSON.parse: Uncaught JS Exception: Error: context should be an object Expected SameValue(«"undefined"», «"o... |
| reviver-forward-modifies-object.js | SyntaxError: Identifier 'replacement' has already been declared |
| reviver-get-name-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-object-define-prop-err.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| reviver-object-delete-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-object-get-prop-from-prototype.js | Test262Error: Expected SameValue(«2», «3») to be true |
| reviver-object-own-keys-err.js | Test262Error: Expected a Test262Error but got a SyntaxError |
| reviver-wrapper.js | no prefix parse function for RBrace found |
| S15.12.2_A1.js | Test262Error: Object.getPrototypeOf("JSON.parse('{"__proto__":[]}')") returns Object.prototype Expected SameValue(«[obje... |
| text-non-string-primitive.js | Test262Error: Expected a SyntaxError but got a TypeError |
| text-object-abrupt.js | no prefix parse function for RBrace found |
| text-object.js | no prefix parse function for RBrace found |
| prop-desc.js | Test262Error: descriptor should not be enumerable |
| basic.js | TypeError: undefined is not a function |
| bigint-raw-json-can-be-stringified.js | Test262Error: Expected SameValue(«9007199254740992», «9007199254740993n») to be true |
| builtin.js | Test262Error: JSON.rawJSON is extensible |
| illegal-empty-and-start-end-chars.js | Test262Error: Expected a SyntaxError but got a TypeError |
| invalid-JSON-text.js | Test262Error: Expected a SyntaxError but got a TypeError |
| length.js | TypeError: Cannot convert undefined or null to object |
| name.js | TypeError: Cannot convert undefined or null to object |
| not-a-constructor.js | Test262Error: isConstructor invoked with a non-function value |
| prop-desc.js | Test262Error: obj should have an own property rawJSON |
| returns-expected-object.js | TypeError: undefined is not a function |
| length.js | Test262Error: descriptor value should be 3; object value should be 3 |
| not-a-constructor.js | Test262Error: isConstructor(JSON.stringify) must return false Expected SameValue(«true», «false») to be true |
| prop-desc.js | Test262Error: descriptor should not be enumerable |
| property-order.js | Test262Error: Expected SameValue(«"{\"p2\":\"p2\",\"add\":\"add\",\"p4\":\"p4\",\"2\":\"2\",\"0\":\"0\",\"1\":\"1\"}"», ... |
| ... | 45 more failures |
