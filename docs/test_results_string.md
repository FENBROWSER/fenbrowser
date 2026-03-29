# Test262 Conformance Results

**Date**: 2026-03-29 14:00:16
**Duration**: 4668ms (4.7s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 315 |
| Failed | 185 |
| Pass Rate | 63.0% |
| Avg/Test | 9.34ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| not-a-constructor.js | Test262Error: isConstructor(String.fromCharCode) must return false Expected SameValue(«true», «false») to be true |
| S15.5.3.2_A1.js | Test262Error: #3: String.fromCharCode.length === 1. Actual: String.fromCharCode.length ===0 |
| S15.5.3.2_A4.js | Test262Error: #1: __fcc__func = String.fromCharCode; var __obj = new __fcc__func(65,66,66,65) lead to throwing exception |
| S9.7_A2.1.js | Test262Error: #7: String.fromCharCode(4294967295).charCodeAt(0) === 65535. Actual: 0 |
| argument-is-not-integer.js | Test262Error: Expected a RangeError to be thrown but no exception was thrown at all |
| argument-is-Symbol.js | Test262Error: Expected a TypeError but got a RangeError |
| fromCodePoint.js | Test262Error: descriptor should not be enumerable |
| length.js | Test262Error: descriptor value should be 1; object value should be 1 |
| not-a-constructor.js | Test262Error: isConstructor(String.fromCodePoint) must return false Expected SameValue(«true», «false») to be true |
| length.js | Test262Error: obj should have an own property length |
| numeric-properties.js | Test262Error: Expected SameValue(«undefined», «"a"») to be true |
| prop-desc.js | Test262Error: descriptor should not be enumerable |
| index-non-numeric-argument-tointeger-invalid.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| index-non-numeric-argument-tointeger.js | Test262Error: s.at(undefined) must return 0 Expected SameValue(«undefined», «"0"») to be true |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.charAt) must return false Expected SameValue(«true», «false») to be true |
| S15.5.4.4_A1_T6.js | Test262Error: #1: var x; new String("lego").charAt(x) === "l". Actual: new String("lego").charAt(x) === |
| S15.5.4.4_A1_T7.js | Test262Error: #1: String("lego").charAt(undefined) === "l". Actual: String("lego").charAt(undefined) === |
| S15.5.4.4_A1_T8.js | Test262Error: #1: String(42).charAt(void 0) === "4". Actual: String(42).charAt(void 0) === |
| S15.5.4.4_A1_T9.js | Test262Error: #1: new String(42).charAt(function(){}()) === "4". Actual: new String(42).charAt(function(){}()) === |
| S15.5.4.4_A7.js | Test262Error: #1.2: undefined = 1 throw a TypeError. Actual: Test262Error: #1: __FACTORY = String.prototype.charAt; "__i... |
| S9.4_A1.js | Test262Error: #1: "abc".charAt(Number.NaN) === "a". Actual:  |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.charCodeAt) must return false Expected SameValue(«true», «false») to be tru... |
| S15.5.4.5_A1_T6.js | Test262Error: #1: var x; new String("lego").charCodeAt(x) === 0x6C. Actual: new String("lego").charCodeAt(x) ===NaN |
| S15.5.4.5_A1_T7.js | Test262Error: #1: String("lego").charCodeAt(undefined) === 0x6C. Actual: String("lego").charCodeAt(undefined) ===NaN |
| S15.5.4.5_A1_T8.js | Test262Error: #1: String(42).charCodeAt(void 0) === 0x34. Actual: String(42).charCodeAt(void 0) ===NaN |
| S15.5.4.5_A1_T9.js | Test262Error: #1: new String(42).charCodeAt(function(){}()) === 0x34. Actual: new String(42).charCodeAt(function(){}()) ... |
| S15.5.4.5_A7.js | Test262Error: #1: __FACTORY = String.prototype.charCodeAt; "__instance = new __FACTORY" lead to throwing exception |
| length.js | Test262Error: descriptor value should be 1; object value should be 1 |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.codePointAt) must return false Expected SameValue(«true», «false») to be tr... |
| return-abrupt-from-symbol-pos-to-integer.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| return-code-unit-coerced-position.js | Test262Error: Expected SameValue(«undefined», «65536») to be true |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.concat) must return false Expected SameValue(«true», «false») to be true |
| S15.5.4.6_A7.js | Test262Error: #1: __FACTORY = String.prototype.concat; "__instance = new __FACTORY" lead throwing exception |
| coerced-values-of-position.js | Error: length ('-2147483648') must be a non-negative value. (Parameter 'length')
Actual value was -2147483648. |
| length.js | Test262Error: descriptor value should be 1; object value should be 1 |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.endsWith) must return false Expected SameValue(«true», «false») to be true |
| return-abrupt-from-position-as-symbol.js | Test262Error: Expected a TypeError but got a Error |
| return-abrupt-from-searchstring-regexp-test.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| return-true-if-searchstring-is-empty.js | Error: length ('-2147483648') must be a non-negative value. (Parameter 'length')
Actual value was -2147483648. |
| searchstring-is-regexp-throws.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| length.js | Test262Error: descriptor value should be 1; object value should be 1 |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.includes) must return false Expected SameValue(«true», «false») to be true |
| return-abrupt-from-position-as-symbol.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| return-abrupt-from-searchstring-regexp-test.js | Test262Error: Expected a Test262Error to be thrown but no exception was thrown at all |
| return-false-with-out-of-bounds-position.js | Error: Index was out of range. Must be non-negative and less than or equal to the size of the collection. (Parameter 'st... |
| searchstring-is-regexp-throws.js | Test262Error: Expected a TypeError to be thrown but no exception was thrown at all |
| String.prototype.includes_FailBadLocation.js | Error: Index was out of range. Must be non-negative and less than or equal to the size of the collection. (Parameter 'st... |
| String.prototype.includes_lengthProp.js | Test262Error: "word".includes.length Expected SameValue(«0», «1») to be true |
| not-a-constructor.js | Test262Error: isConstructor(String.prototype.indexOf) must return false Expected SameValue(«true», «false») to be true |
| position-tointeger-bigint.js | Test262Error: ToInteger: BigInt => TypeError Expected a TypeError to be thrown but no exception was thrown at all |
| ... | 135 more failures |
