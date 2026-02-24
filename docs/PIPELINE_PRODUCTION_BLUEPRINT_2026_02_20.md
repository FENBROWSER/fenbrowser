# Pipeline Production Blueprint (2026-02-20)

Motto: **Architecture is Destiny**

## 1. Start-to-Finish Browser Pipeline Comparison

### Chromium model (high level)
1. Network/loader process fetches bytes with policy checks.
2. Renderer process tokenizes/parses HTML incrementally.
3. Style engine computes cascade with invalidation graph.
4. Layout builds geometry trees and updates incrementally.
5. Paint builds display lists.
6. Compositor/raster executes tiles/layers and presents frame.

### Firefox model (high level)
1. Parent/content process split for navigation and isolation.
2. Streaming parse + DOM construction with spec error recovery.
3. Stylo computes styles with rule tree and invalidation.
4. Gecko layout constructs frame tree and reflows incrementally.
5. Display list build + WebRender scene/raster pipeline.
6. Frame scheduling/composition/present.

### Ladybird model (high level)
1. Strict module layering (DOM/CSS/Layout/Paint/Platform).
2. Spec-driven parse + cascade.
3. Layout tree build and formatting-context execution.
4. Display list generation.
5. Rendering backend paints and presents.

### Fen target model
1. ResourceManager/network policy gate.
2. Parser/tree-builder to DOM.
3. Cascade/computed styles.
4. Layout/box tree.
5. Paint tree/display list.
6. Rasterize and present through host compositor.

## 2. Fen Pipeline Maturity (1-100)

1. Network + policy gate: **82/100**
2. Parse/tokenize correctness: **74/100**
3. Style/cascade correctness: **76/100**
4. Layout/formatting contexts: **72/100**
5. Paint tree + z-order: **79/100**
6. Raster/present lifecycle invariants: **68/100** (improved in this tranche)
7. Process isolation architecture: **70/100** (seam + brokered path exists, still maturing)

Overall production readiness for pipeline integrity: **74/100**

## 3. Gaps To Close (Production, No Site Hacks)

1. Keep stage boundaries exception-safe in all frame paths.
2. Ensure full stage coverage (style/layout/paint/raster/present) in runtime telemetry.
3. Enforce per-stage timing accuracy (true stage duration, not cumulative frame duration).
4. Keep stage lifecycle test-covered so regressions fail CI.
5. Continue replacing time-based rendering retries with deterministic dirty/event triggers.

## 4. Implemented in This Tranche

1. Scoped pipeline lifecycle guards added in Core:
   - `FenBrowser.Core/Engine/PipelineContext.cs`
   - New `BeginScopedFrame()` and `BeginScopedStage(...)` for automatic stage/frame closure.
2. Stage timing fixed to per-stage elapsed timing:
   - `FenBrowser.Core/Engine/PipelineContext.cs`
3. Renderer now runs full stage chain including raster + present:
   - `FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs`
4. Regression tests added for scoped lifecycle + timing:
   - `FenBrowser.Tests/Engine/PipelineContextTests.cs`

## 5. Next Hardening Steps

1. Attach parser/tokenizer stage telemetry to navigation pipeline execution path.
2. Replace delayed fallback repaint (`Task.Delay`) paths with dirty-state checkpoints.
3. Promote stage timing snapshots to diagnostics logs and CI thresholds.
4. Add multi-thread pipeline ownership rules (UI thread vs render thread) into invariant tests.
