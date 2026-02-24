# Web Compatibility Production Plan

Objective: ship broad site compatibility through standards-compliant engine behavior, not site/domain/class hacks.

## 1. Non-Negotiable Policy

1. No domain-name checks in layout/cascade/paint paths.
2. No CSS class-name rewrites in computed style generation.
3. Any temporary intervention must be:
   - Feature-flagged
   - Telemetry-backed
   - Time-boxed with removal criteria
   - Covered by a standards regression test

## 2. Feature Set (Model from Chromium/Firefox/Ladybird)

These are engine-level capabilities to implement and harden.

### Engine-Derived Baseline

1. Chromium pattern: intervention framework is centralized and temporary.
   - How it works: engine behavior is fixed first; only last-resort compatibility interventions are gated, measured, and deleted after root-cause fixes.
2. Firefox pattern: standards and tests drive behavior.
   - How it works: layout/cascade changes land with spec-linked tests (WPT/regression), reducing drift and avoiding "silent hacks."
3. Ladybird pattern: strict layering and clear ownership boundaries.
   - How it works: parser/cascade/layout/paint are isolated modules with explicit contracts, so fixes stay generic and do not leak site assumptions into the pipeline.

### A. Parsing and Cascade Correctness

1. Robust HTML error recovery and quirks/limited-quirks/no-quirks mode.
2. Full selector semantics used by modern sites (`:is`, `:where`, `:not`, `:has`, state pseudos).
3. Correct cascade ordering: origin, `!important`, specificity, source order, layers, scope.
4. Correct `display` resolution for HTML/custom elements and formatting-context creation.

How it works in production engines: layout breakages are prevented by accurate parser+cascade behavior before any special-case logic is needed.

### B. Layout and Intrinsic Sizing

1. Spec-accurate float interaction and clear handling.
2. Intrinsic sizing and shrink-to-fit for replaced/inline/float content.
3. Stable block/inline/flex/grid interaction and containing-block resolution.
4. Writing-mode-aware logical sizing and margin/padding conversion.

How it works in production engines: they solve classes of layout problems once in the layout algorithm, then thousands of sites improve automatically.

### C. Compatibility Intervention System (Last Resort)

1. Centralized intervention registry (single subsystem).
2. Interventions keyed by behavior class (not domain/class names).
3. Runtime kill-switch and per-intervention metrics.
4. Auto-expiry/removal workflow after root-cause engine fix lands.

How it works in production engines: interventions are controlled, observable, and removable; they do not become permanent architecture.

### D. Verification Strategy

1. WPT-like behavior tests per feature class.
2. Regression snapshots for representative page patterns.
3. CI gates:
   - No new hardcoded site signatures in engine paths.
   - No compatibility regressions on core behavior suites.

How it works in production engines: compatibility is guarded by continuous conformance testing, not manual visual tuning.

## 3. Fen Execution Plan

### Phase A: Remove hacks and freeze policy

1. Remove domain/class-specific computed-style rewrites.
2. Add guard tests that fail if those rewrites return.

### Phase B: Raise standards coverage

1. Prioritize missing parser/cascade/layout features causing top breakages.
2. For each fix: add regression test before and after implementation.
3. Prioritize these high-impact classes first:
   - Intrinsic sizing and shrink-to-fit parity.
   - Percentage/auto height chains and containing-block correctness.
   - Float/clear interaction and margin-collapsing edge cases.
   - Form control defaults and replaced-element sizing behavior.

### Phase C: Controlled interventions (if still needed)

1. Add intervention framework with metrics.
2. Keep interventions minimal and scheduled for deletion.

## 3.1 Current Implementation Status (2026-02-20)

1. Phase A: completed in core cascade/UA/layout paths.
   - Site/domain/class-specific rewrites removed from:
   - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
   - `FenBrowser.FenEngine/Rendering/UserAgent/UAStyleProvider.cs`
   - `FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs`
2. Phase B (high-impact tranche): in progress with engine-level fixes.
   - Replaced-element sizing unified:
   - `FenBrowser.FenEngine/Layout/ReplacedElementSizing.cs`
   - Float exclusion-band placement and intrusion handling hardened:
   - `FenBrowser.FenEngine/Layout/Contexts/BlockFormattingContext.cs`
   - `FenBrowser.FenEngine/Layout/Contexts/FloatManager.cs`
3. Phase C foundation: implemented.
   - Central registry with global kill-switch, per-intervention metrics, and expiry checks:
   - `FenBrowser.FenEngine/Compatibility/WebCompatibilityInterventions.cs`
   - Cascade-stage wiring:
   - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
   - Regression coverage:
   - `FenBrowser.Tests/Engine/WebCompatibilityInterventionRegistryTests.cs`
   - `FenBrowser.Tests/Engine/WebCompatibilityGuardrailsTests.cs`

## 4. Definition of Done

1. Site compatibility improvements are achieved through generic engine logic.
2. No active site/domain/class hacks in core rendering/layout/cascade.
3. Each compatibility fix maps to tests and technical-reference documentation updates.

Architecture is Destiny.
