# FenBrowser Compliance Matrix

State as of: 2026-04-30
Owner: Architecture Track

## Status Model

- `Unsupported`: no production implementation; must fail cleanly.
- `Partial`: implemented but not spec-complete.
- `Provisional`: spec-shaped implementation with known edge gaps.
- `Complete`: spec-complete for scoped capability and regression-guarded.

## Severity Model

- `P0`: correctness or security blocker.
- `P1`: major standards or architecture risk.
- `P2`: important parity gap, not release-blocking alone.
- `P3`: improvement or coverage expansion.

## Matrix

| Capability ID | Subsystem | Spec Reference | Owner | Status | Severity | Verification Target |
| --- | --- | --- | --- | --- | --- | --- |
| EVENTLOOP-MACROTASK-01 | Event Loop | WHATWG HTML event loops | `FenBrowser.FenEngine` | Partial | P0 | Deterministic task ordering tests |
| EVENTLOOP-MICROTASK-01 | Event Loop | WHATWG HTML microtask checkpoint | `FenBrowser.FenEngine` | Partial | P0 | Promise/microtask drain tests |
| DOM-NODELIST-LIVE-01 | DOM | DOM Living Standard NodeList behavior | `FenBrowser.Core` + `FenBrowser.FenEngine` | Provisional | P1 | DOM collection regression tests |
| DOM-MUTATION-OBSERVER-01 | DOM | DOM mutation observer model | `FenBrowser.Core` + `FenBrowser.FenEngine` | Partial | P1 | Mutation observer conformance slice |
| CSS-SYNTAX-TOKENIZATION-01 | CSS Parsing | CSS Syntax Level 3 tokenization | `FenBrowser.FenEngine` | Partial | P0 | Tokenizer regression suite |
| CSS-PARSER-RECOVERY-01 | CSS Parsing | CSS Syntax parse error recovery | `FenBrowser.FenEngine` | Provisional | P1 | Parser recovery regressions |
| CSS-VALUES-COMPUTE-01 | CSS Values | CSS Values and Units Level 4 parse/compute | `FenBrowser.FenEngine` | Partial | P0 | Value parsing/computation regressions |
| CSS-CASCADE-ORDER-01 | Cascade | CSS Cascade origin/specificity/order | `FenBrowser.FenEngine` | Complete | P0 | CascadeModernTests + inline-important precedence regression suite |
| CSS-CASCADE-LAYERS-01 | Cascade | CSS Cascade Level 5 layer ordering and important inversion | `FenBrowser.FenEngine` | Complete | P0 | CascadeModernTests layer ordering + important inversion slice |
| CSS-SELECTOR-MATCH-01 | Selectors | Selectors Level 4 | `FenBrowser.FenEngine` | Complete | P1 | SelectorMatcherConformanceTests + DOM selector regression suite |
| CSS-SELECTOR-ADVANCED-01 | Selectors | Selectors Level 4 functional pseudo-classes | `FenBrowser.FenEngine` | Provisional | P1 | Advanced selector conformance slice |
| CSSOM-STYLESHEET-LIST-01 | CSSOM | CSSOM `document.styleSheets` and StyleSheetList behavior | `FenBrowser.FenEngine` | Complete | P2 | HtmlCollectionTests + StyleSheetList live/identity regression tests |
| CSSOM-STYLESHEET-MUTATION-01 | CSSOM | CSSOM `insertRule`/`deleteRule` + `cssRules` synchronization | `FenBrowser.FenEngine` | Complete | P1 | InlineStyleSheetBridgeTests live-rule + mutation regression tests |
| CSSOM-COMPUTEDSTYLE-API-01 | CSSOM | CSSOM `getComputedStyle` computed-value exposure | `FenBrowser.FenEngine` | Partial | P1 | Computed style API regression tests |
| CSS-CONTAINMENT-QUERY-01 | Containment/Queries | CSS Containment Level 3 + Conditional Rules Level 5 | `FenBrowser.FenEngine` | Provisional | P1 | Container query conformance slice |
| LAYOUT-FORMATTING-CONTEXT-01 | Layout | CSS2.1 formatting contexts | `FenBrowser.FenEngine` | Partial | P1 | Block/inline layout regressions |
| LAYOUT-FLEX-ALGO-01 | Layout | CSS Flexbox | `FenBrowser.FenEngine` | Provisional | P1 | Flex layout focused tests |
| LAYOUT-GRID-TRACKS-01 | Layout | CSS Grid sizing and placement | `FenBrowser.FenEngine` | Provisional | P1 | Grid layout focused tests |
| LAYOUT-TABLE-ALGO-01 | Layout | CSS 2.2 table layout algorithms | `FenBrowser.FenEngine` | Provisional | P1 | Table layout focused tests |
| LAYOUT-MULTICOL-ALGO-01 | Layout | CSS Multi-column Layout Level 1 | `FenBrowser.FenEngine` | Provisional | P1 | Multi-column layout focused tests |
| LAYOUT-POSITIONING-SIZING-01 | Layout | CSS2.2 positioned layout and used-size resolution | `FenBrowser.FenEngine` | Partial | P0 | Positioning/sizing regression suite |
| PAINT-STACKING-ORDER-01 | Painting | CSS2.1 paint order | `FenBrowser.FenEngine` | Partial | P0 | Paint tree order regressions |
| PAINT-COMPOSITING-EFFECTS-01 | Painting | CSS Compositing and Blending Level 1 + CSS Masking Level 1 | `FenBrowser.FenEngine` | Provisional | P1 | Renderer compositing/effects regressions |
| ANIMATION-KEYFRAMES-ENGINE-01 | Animations/Transitions | CSS Animations Level 1 + CSS Transitions Level 2 runtime semantics | `FenBrowser.FenEngine` | Provisional | P1 | Animation/transition runtime regression slice |
| TYPEDOM-STYLEVALUE-01 | Typed OM | CSS Typed OM Level 1 typed value objects | `FenBrowser.FenEngine` | Unsupported | P2 | Typed OM typed value conformance slice |
| TYPEDOM-STYLEMAP-01 | Typed OM | CSS Typed OM `attributeStyleMap`/`computedStyleMap` | `FenBrowser.FenEngine` | Unsupported | P2 | Typed OM style map conformance slice |
| HOUDINI-WORKLET-INTEGRATION-01 | Houdini | CSS Painting API + Worklets integration and invalidation hooks | `FenBrowser.FenEngine` + `FenBrowser.Host` | Unsupported | P2 | Houdini worklet integration tests |
| JS-BUILTINS-ES2024-01 | JavaScript | ECMA-262 built-ins | `FenBrowser.FenEngine` | Partial | P1 | Test262 tracked subset |
| JS-RUNTIME-EXCEPTION-01 | JavaScript | ECMA-262 abrupt completions | `FenBrowser.FenEngine` | Provisional | P1 | VM exception flow tests |
| WEBIDL-GENERATION-ORDER-01 | WebIDL | Web IDL deterministic generation | `FenBrowser.Core` | Provisional | P2 | Generator hash and verify tests |
| FETCH-CORS-POLICY-01 | Network/Security | WHATWG Fetch + CORS | `FenBrowser.Core` + `FenBrowser.Host` | Partial | P0 | Network policy tests |
| SECURITY-CSP-ENFORCEMENT-01 | Security | CSP processing model | `FenBrowser.Core` + `FenBrowser.Host` | Partial | P0 | CSP violation and block tests |
| SECURITY-COOKIE-MODEL-01 | Security | RFC6265 cookie semantics | `FenBrowser.Core` + `FenBrowser.Host` | Partial | P1 | Cookie policy regressions |
| PROCESS-IPC-HANDSHAKE-01 | Process Isolation | Fen process startup contract | `FenBrowser.Host` | Provisional | P0 | Child startup/fail-closed tests |
| PROCESS-SANDBOX-FAILCLOSED-01 | Process Isolation | Fen sandbox policy contract | `FenBrowser.Host` + `FenBrowser.Core` | Provisional | P0 | Sandbox deny-path tests |
| A11Y-TREE-ROLE-MAP-01 | Accessibility | ARIA + platform mapping contract | `FenBrowser.FenEngine` + `FenBrowser.Host` | Partial | P2 | Accessibility mapping tests |
| DEVTOOLS-RUNTIME-SIGNAL-01 | DevTools | CDP runtime/debugger contract | `FenBrowser.DevTools` + `FenBrowser.Host` | Partial | P2 | Protocol integration tests |
| VERIFY-WPT-TRUTH-01 | Verification | WPT harness truthfulness | `FenBrowser.Tests` + tooling | Partial | P1 | WPT deterministic runner checks |
| VERIFY-TEST262-TRUTH-01 | Verification | Test262 harness truthfulness | `FenBrowser.Test262` + tooling | Partial | P1 | Test262 deterministic runner checks |

## Completion Gate For Any Capability

A capability can move to `Complete` only when all are true:

- Spec reference is explicit and current.
- Owner path is explicit.
- Focused deterministic tests pass.
- Cross-boundary tests pass when IPC/process/security is involved.
- No site-specific rule is required to make it pass.
