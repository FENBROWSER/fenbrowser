# AI Dual-Model Audit & Integration Plan (Gemini + Codex)

To elevate FenBrowser to a production-grade system, we must leverage the strengths of multiple AI models working in tandem. This plan outlines the strategy for integrating **Gemini** and **Codex** models side-by-side for continuous auditing, development, and security validation.

## 1. Engine Score Context

Based on our recent engine architecture review, FenBrowser (FenEngine) currently holds an overall architectural score of **78.3**.

| Engine                     |  Perf  | Security | Modularity | Standards | **Overall Score** |
| :------------------------- | :----: | :------: | :--------: | :-------: | :---------------: |
| **V8** (Chrome/Node)       |   98   |    85    |     70     |    99     |     **88.0**      |
| **SpiderMonkey** (Firefox) |   95   |    88    |     65     |    98     |     **86.5**      |
| **FenEngine** (Current)    | **60** |  **92**  |   **96**   |  **65**   |     **78.3**      |
| **LibJS** (Ladybird)       |   30   |    82    |     75     |    92     |     **69.8**      |

**Analysis**: We currently beat Ladybird (69.8) and have industry-leading scores in _Security (92)_ and _Modularity (96)_. However, we are behind Chrome (88.0) and Firefox (86.5) primarily due to _Performance (60)_ and _Standards/Test262 Compliance (65)_. The side-by-side AI model approach will target these exact weak points.

## 2. Dual-Model Side-by-Side Strategy

By running Gemini and Codex concurrently, we can divide rendering/engine complex tasks based on each model's innate strengths.

### Model Roles

- **Gemini Model (Architect & Security Auditor)**
  - **Focus**: Systems architecture, security gap analysis, ECMAScript standard compliance strategy, and HTML5/CSS3 specification interpretation.
  - **Tasks**: Reviewing engine designs, analyzing Test262 gap reports, and ensuring strict adherence to the project's security/privacy motto.
- **Codex Model (Implementation & Perf Tuner)**
  - **Focus**: Low-level C# code generation, micro-optimizations (like the recent IL Local Mapping), and rapid unit test generation.
  - **Tasks**: Implementing the architectural plans drafted by Gemini, writing performance-critical bytecode VM instructions, and fixing specific Test262 failures.

### Cross-Validation Workflow

To prevent AI hallucinations and ensure production-grade reliability:

1.  **Phase 1: Planning (Gemini)**. Gemini analyzes a failing standard (e.g., `Array.fromAsync`) and drafts the spec-compliant architectural approach.
2.  **Phase 2: Execution (Codex)**. Codex takes Gemini's spec and implements the highly-optimized C# code in `FenRuntime.cs`.
3.  **Phase 3: Cross-Audit (Side-by-Side)**.
    - Gemini runs a security and specification audit on Codex's generated code.
    - Codex generates stress tests and benchmarks to validate Gemini's architectural logic.

## 3. Implementation Steps

1.  **CI/CD Pipeline Integration**: Set up build hooks where PRs are automatically routed to both models.
2.  **Test262 Coverage Push**: Feed the failing Test262 chunks (currently yielding ~16.8% overall pass rate) to the dual-model system, chunk by chunk.
3.  **Performance Profiling**: Codex to analyze `VirtualMachine.cs` and rendering hot-paths weekly, while Gemini audits the changes to ensure no memory safety bounds are compromised.

## 4. HTML/CSS 100% Execution Program (Mandatory)

We are now explicitly targeting **100% HTML5 parsing**, **100% CSS parsing**, and **100% CSS rendering behavior** for the committed standards scope.

### 4.1 Current Reality (from repo evidence)

- HTML5 parsing is documented around **~90%** in comparison baselines.
- CSS properties parsing is documented around **~60%**.
- CSS properties rendering is documented around **~30%**.
- Layout algorithm completeness is documented around **~67%**.
- Some docs show tranche scores at `90/100` for HTML and CSS subsystems; these represent pipeline maturity, not full standards breadth.

### 4.2 Hard Definition of "100%"

No subsystem is allowed to claim 100% without passing objective truth sources:

1.  **HTML5 = 100%**
    - html5lib/tree-construction conformance suite: **100% pass** on the enabled corpus.
    - WPT HTML parsing/tree-construction set: **100% pass** on the committed subset.
    - No tokenizer state marked TODO/stub in production parser path.
2.  **CSS Parse = 100%**
    - Every property in the committed CSS target matrix parses and computes correctly.
    - At-rule/media/query syntax in committed matrix has no parser TODO/stubs.
    - Parse + serialize + reparse round-trip tests pass for committed matrix.
3.  **CSS Render = 100%**
    - Every parsed property in committed matrix has deterministic layout/paint/compositor effect (or spec-defined no-op).
    - WPT visual/layout behavior tests for committed matrix pass.
    - No "parsed but not ticked/not applied" behavior for animations/transitions/layout-critical features.

### 4.3 Non-Negotiable Prerequisite: Verification Truth First

Before claiming progress to 100, we must remove verification ambiguity:

1.  Complete `WPTTestRunner` production execution path and verdict classification.
2.  Wire WPT execution into CI reporting (TAP/JUnit + trend history).
3.  Publish nightly HTML/CSS pass-rate snapshots in docs.

### 4.4 Tranche Order (Execution Sequence)

1.  **TRUTH-1 (Verification Gate)**
    - Deliver stable automated WPT/html5lib execution and reporting.
    - Output: reproducible baseline percentages from CI, not manual estimates.
2.  **HTML-100**
    - Close remaining HTML tokenizer/tree-construction gaps.
    - Lock parser invariants with regression corpus and pathological-input tests.
3.  **CSS-PARSE-100**
    - Close high-impact missing syntax/features first: `@layer`, `@container`, math functions (`min/max/clamp`), robust `calc()` expressions, remaining selector/at-rule gaps.
    - Expand property grammar coverage to full committed matrix.
4.  **CSS-RENDER-100**
    - Close parse/render disconnect by mapping computed styles to layout/paint behavior.
    - Priority classes: animation/transition ticking, sticky/subgrid/multi-column, container-query-driven style invalidation, logical properties behavior parity.
5.  **SUSTAIN-100**
    - Keep pass rates at 100 via CI gates, failure triage SLA, and weekly compatibility audits.

### 4.5 Working Rules for the 100% Program

1.  No site-specific hacks in parser/cascade/layout/paint paths.
2.  Every fix must ship with regression tests and WPT-linked evidence.
3.  No TODO/stub placeholders in hot paths for committed feature matrix.
4.  Documentation updates are mandatory in the same change series (Living Bible policy).

### 4.6 Immediate 72-Hour Actions

1.  Freeze and publish a single authoritative feature matrix for HTML/CSS parse/render status.
2.  Finish WPT execution truth path in CI and generate first machine-derived baseline.
3.  Open and execute the first high-impact closure bundle:
    - CSS animations/transitions ticking verification and pipeline wiring checks.
    - `@layer` and `@container` parser/cascade integration plan.
    - `position: sticky` and multi-column/subgrid implementation audit and task split.
