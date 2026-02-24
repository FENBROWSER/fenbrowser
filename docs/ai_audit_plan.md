# AI Dual-Model Audit & Integration Plan (Gemini + Codex)

To elevate FenBrowser to a production-grade system, we must leverage the strengths of multiple AI models working in tandem. This plan outlines the strategy for integrating **Gemini** and **Codex** models side-by-side for continuous auditing, development, and security validation.

## 1. Engine Score Context

Based on our recent `engine_analysis.md`, FenBrowser (FenEngine) currently holds an overall architectural score of **78.3**.

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
