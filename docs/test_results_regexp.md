# Test262 Conformance Results

**Date**: 2026-03-29 14:01:43
**Duration**: 61117ms (61.1s)

## Summary

| Metric | Value |
|--------|-------|
| Total  | 500 |
| Passed | 21 |
| Failed | 479 |
| Pass Rate | 4.2% |
| Avg/Test | 122.23ms |

## Failures (top 50)

| Test | Error |
|------|-------|
| 15.10.2.15-6-1.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| 15.10.2.5-3-1.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| 15.10.4.1-2.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| 15.10.4.1-3.js | Test262Error: Expected a SyntaxError to be thrown but no exception was thrown at all |
| call_with_non_regexp_same_constructor.js | Test262Error: RegExp returns its input argument Expected SameValue(«/[object Object]/», «[object Object]») to be true |
| character-class-escape-non-whitespace-u180e.js | Test262Error: Non WhiteSpace character: \u180E Expected SameValue(«"᠎"», «"test262"») to be true |
| character-class-escape-non-whitespace.js | Test262Error: Non WhiteSpace character, charCode: 0 Expected SameValue(«"\u0000"», «"test262"») to be true |
| character-class-digit-class-escape-negative-cases.js | Test timed out after 10000ms |
| character-class-non-digit-class-escape-positive-cases.js | Test timed out after 10000ms |
| character-class-non-whitespace-class-escape-negative-cases.js | Test262Error: Expected no match, but matched: 0xfeff,0xfeff,0xfeff Expected SameValue(«3», «0») to be true |
| character-class-non-whitespace-class-escape-positive-cases.js | Test timed out after 10000ms |
| character-class-non-word-class-escape-positive-cases.js | Test timed out after 10000ms |
| character-class-whitespace-class-escape-negative-cases.js | Test timed out after 10000ms |
| character-class-whitespace-class-escape-positive-cases.js | Test262Error: Expected full match, but did not match: 0xfeff,0xfeff,0xfeff Expected SameValue(«3», «0») to be true |
| character-class-word-class-escape-negative-cases.js | Test timed out after 10000ms |
| with-dotall-unicode.js | Test262Error: Supplementary plane matched by a single . |
| without-dotall-unicode.js | Test262Error: Supplementary plane matched by a single . |
| without-dotall.js | Test262Error: Expected true but got false |
| duplicate-flags.js | Test262Error: duplicate g Expected a SyntaxError to be thrown but no exception was thrown at all |
| duplicate-named-capturing-groups-syntax.js | Test262Error: Duplicate named capturing groups in the same alternative do not parse Expected a SyntaxError to be thrown ... |
| early-err-modifiers-code-point-repeat-i-1.js | Test262Error: RegExp("(?ii:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-code-point-repeat-i-2.js | Test262Error: RegExp("(?imsi:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-arbitrary.js | Test262Error: RegExp("(?1:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-combining-i.js | Test262Error: RegExp("(?iͥ:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-combining-m.js | Test262Error: RegExp("(?mͫ:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-combining-s.js | Test262Error: RegExp("(?s̀:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-d.js | Test262Error: RegExp("(?d:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-g.js | Test262Error: RegExp("(?g:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-non-display-1.js | no prefix parse function for Illegal found
[ParseCallArguments] expected next token to be RParen, got Eof instead (cur=I... |
| early-err-modifiers-other-code-point-non-display-2.js | Test262Error: RegExp("(?s‎:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-non-flag.js | Test262Error: RegExp("(?Q:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-u.js | Test262Error: RegExp("(?u:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-uppercase-I.js | Test262Error: RegExp("(?I:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-y.js | Test262Error: RegExp("(?y:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-zwj.js | Test262Error: RegExp("(?s‍:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-zwnbsp.js | Test262Error: RegExp("(?s﻿:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-other-code-point-zwnj.js | Test262Error: RegExp("(?s‌:a)", ""):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-should-not-case-fold-i.js | Test262Error: RegExp("(?I:a)", "i"):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-should-not-case-fold-m.js | Test262Error: RegExp("(?M:a)", "i"):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-should-not-case-fold-s.js | Test262Error: RegExp("(?S:a)", "i"):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-should-not-unicode-case-fold-i.js | Test262Error: RegExp("(?İ:a)", "iu"):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| early-err-modifiers-should-not-unicode-case-fold-s.js | Test262Error: RegExp("(?ſ:a)", "u"):  Expected a SyntaxError to be thrown but no exception was thrown at all |
| cross-realm.js | Test262Error: other.RegExp.escape is a function Expected SameValue(«"undefined"», «"function"») to be true |
| escaped-control-characters.js | TypeError: undefined is not a function |
| escaped-lineterminator.js | TypeError: undefined is not a function |
| escaped-otherpunctuators.js | TypeError: undefined is not a function |
| escaped-solidus-character-mixed.js | TypeError: undefined is not a function |
| escaped-solidus-character-simple.js | TypeError: undefined is not a function |
| escaped-surrogates.js | TypeError: undefined is not a function |
| escaped-syntax-characters-mixed.js | TypeError: undefined is not a function |
| ... | 429 more failures |
