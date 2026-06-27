# FenBrowser — Test Baseline

> Auto-generated Gate 0 Reality Audit. Last refreshed: 2026-06-27.
> Evidence policy: numbers come from actual test runs, not estimates.

## 1. Build

| Metric | Value |
|--------|-------|
| Solution | FenBrowser.sln (14 projects) |
| Target framework | net10.0 |
| Build result | **0 errors, 13 warnings** |
| Warnings | All in test projects: CS0618 (obsolete Node.Text/Node.ComputedStyle/Element.Attr), xUnit2012/xUnit2031 analyzer suggestions |

## 2. Unit Tests

### FenBrowser.Tests (browser engine)

| Metric | Value |
|--------|-------|
| Passed | **566** |
| Failed | **16** |
| Total | **582** |
| Pass rate | **97.3%** |

Failing tests by area:
- WebDriver: 9 (shadow root, element commands, navigation, cookies, multi-session, actions)
- Scripting/FenJS XMLHttpRequest: 5 (PromiseThen, ArrowPromiseChain, ObjectLiteral, PromiseConstructor, Fetch)
- Core/GoogleSnapshotDiagnostics: 2 (snapshot analysis null refs)
- Core/Layout: 1 (HeightResolutionTests.InlineButton_WithNestedFlexContent)
- Core/P2ClosureContract: 1 (EngineLog_PerDocumentCounters)

### FenBrowser.Js.Tests (JS engine)

| Metric | Value |
|--------|-------|
| Passed | **752** |
| Failed | **6** |
| Total | **758** |
| Pass rate | **99.2%** |

Failing tests:
- ModuleParserTests.ExportNamedListProducesOneEntryEach (1)

## 3. Test262 Conformance

| Metric | Value |
|--------|-------|
| Overall | **50842/55220 = 92.07%** |
| Categories total | 1772 |
| Categories at 100% | 1474 |
| Categories below 100% | 298 |

### Top failure clusters (by fail count, >50 fails)

| Category | Fail | Pass% | Root cause type |
|----------|------|-------|-----------------|
| staging | 566 | 61.8% | ES2025 proposals, mixed |
| built-ins/RegExp | 343 | 85.5% | Regex VM gaps, unicode property escapes, matchAll indices |
| built-ins/Temporal | 268 | 94.2% | relativeTo, DST-aware diff, until/since largestUnit, non-ISO calendar edge cases |
| language/eval-code/direct | 211 | 63.1% | Eval scope resolution, var-binding semantics |
| built-ins/TypedArray | 166 | 88.6% | Species, validation, resizable buffers |
| language/statements/class | 166 | 96.2% | Private field brand checks, super() ordering |
| built-ins/TypedArrayConstructors | 154 | 79.1% | Constructor species, this-check |
| intl402/NumberFormat | 140 | 43.8% | Missing resolvedOptions, formatRange, formatToParts gaps |
| intl402/Temporal | 138 | 93.2% | Calendar-aware Intl formatting for Temporal types |
| intl402/DateTimeFormat | 122 | 50.0% | Missing resolvedOptions, formatToParts |
| built-ins/Atomics | 110 | 71.8% | Atomic operations on SharedArrayBuffer |
| built-ins/Array | 96 | 96.9% | 2^53-1 length/index edge cases, species |
| language/expressions/class | 96 | 97.6% | Class expression scoping, private names |
| language/import/import-defer | 89 | 11.9% | Deferred import proposal (ES2025) |
| built-ins/String | 76 | 93.8% | matchAll, replaceAll with regexps |
| language/statements/for-of | 74 | 90.1% | Iterator close on destructuring, for-of head let leak |
| built-ins/Function | 66 | 87.0% | Function.prototype.toString source text |
| built-ins/Object | 65 | 98.1% | 2^53-1 key edge cases |

## 4. WPT Conformance

| Metric | Value |
|--------|-------|
| Runner | Upstream wptrunner + wptrunner_fenbrowser plugin (WebDriver-based) |
| Known result | dom/lists: 180/189 = 95.2% |
| Full baseline | **Not yet run** — needs full category sweep |
| Runner caveat | Must pin FEN_BROWSER_SCRIPT_ENGINE=legacy for WebDriver (dual-engine split-brain issue) |

## 5. Real-Site Smoke Testing

| Site | Elements | With Layout | Missing Rect | Zero-Area (w/content) | Verdict |
|------|----------|-------------|--------------|----------------------|---------|
| HackerNews (news.ycombinator.com) | 816 | 777 (95%) | 31 | 97 (0) | 🟡 Mostly renders, minor gaps |
| React docs (react.dev) | 1843 | 1240 (67%) | 497 | 11 (4) | 🟠 Significant missing elements |
| GitHub (github.com) | 1959 | 1090 (56%) | 588 | 194 (102) | 🔴 Major layout gaps, many zero-area with content |
| x.com | 81 | 10 (12%) | 26 | 0 (0) | 🔴 Severely broken, scripts barely execute |

## 6. Known Regressions from Test Failures

| Test | Area | Type |
|------|------|------|
| WebDriverContractTests (9 fails) | WebDriver | Shadow DOM, element refs, navigation, cookies |
| FenJsXmlHttpRequestTests (5 fails) | Scripting | Promise/Fetch in hosted browser engine |
| GoogleSnapshotDiagnosticsTests (2 fails) | Diagnostics | Null-ref in snapshot analysis |
| HeightResolutionTests.InlineButton | Layout | Flex + inline button height |
| P2ClosureContractTests.EngineLog | Core | Per-document engine log aggregation |

## 7. html5lib Conformance

**Not yet baselined.** The `html5lib-tests/` directory exists with upstream test data. Runner needs to be verified.

## 8. CSS Test Conformance

**Not yet formally baselined.** WPT CSS tests can be run via wptrunner. Acid2 smiley face assembles (commit 5321ce29).

## 9. WebDriver Test Conformance

| Metric | Value |
|--------|-------|
| WebDriver-specific tests | ~20 in FenBrowser.Tests |
| Pass rate | ~55% (11/20 estimated) |
| Known gaps | Shadow root commands, cross-session element refs, cookie isolation |
