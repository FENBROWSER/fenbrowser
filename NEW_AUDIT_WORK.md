# NEW_AUDIT_WORK

## Purpose
This document replaces the prior closure ledger.

The earlier `NEW_AUDIT_WORK.md` tracked the architecture, diagnostics, and thin-contract work that came out of `NEW_AUDIT.md`. That ledger is complete.

This new document is the deeper rendering and performance audit ledger for fenbrowser.

It exists to answer one question:

What must change for fen to behave like a production-grade browser under real page load, real interaction, real frame pressure, and real future scale?

This is not a generic optimization wishlist.
It is the execution ledger for rendering correctness under pressure, frame-time discipline, memory discipline, scheduling, raster strategy, text quality, and production observability.

## Status
- Previous architecture/hardening ledger: completed on `2026-03-30`.
- This rendering/performance ledger: `in progress`.
- P0: completed on `2026-03-30`.
- P1: completed on `2026-03-30`.
- P2: open.

## Current Runtime Reality
Ground truth from the clean-state Google host run on `2026-03-30` after the P1 closure pass:

- `debug_screenshot.png` is visibly painted and shows the Google homepage shell instead of a blank or mostly unpainted frame.
- Diagnostics now converge under workspace-root `logs`, including:
  - `logs/raw_source_20260330_131408.html`
  - `logs/engine_source_20260330_131429.html`
  - `logs/rendered_text_20260330_131429.txt`
  - `logs/fenbrowser_20260330_131407.log`
  - `logs/fenbrowser_20260330_131407.jsonl`
- Verification truth is now authoritative instead of being overwritten by later subresource/script churn:
  - raw-source artifact size: `187710` bytes
  - verification network result: `PASS (187527 bytes)`
  - rendered-text artifact size: `517` characters
  - verification visible-text result: `PASS (323 characters)`
- The renderer is still materially over budget on first/full frames:
  - first commit total: `557.24ms`
  - first commit layout: `236.28ms`
  - first commit paint: `211.22ms`
  - first commit raster: `105.21ms`
- Repeated full render work is no longer the steady-state path for ordinary animation frames once the page has converged:
  - first commit: `rasterMode=Full`, `baseFrameSeeded=false`, `watchdogTriggered=true`
  - converged animation tail: `rasterMode=Damage`, `baseFrameSeeded=true`, `layoutUpdated=false`, `usedDamageRasterization=true`, `watchdogTriggered=false`
  - tail frame timings now settle around `2.64ms` to `3.15ms`
- Watchdog warnings are no longer the steady-state norm, but isolated convergence outliers still appear (`frameSequence` `5`, `20`, `33`, `79`). That remaining budget discipline is now P2 work, not a P1 blocker.
- Layout constraint ownership remains explicit in logs from the P0 pass. The remaining `Raw=0 -> viewport` fallback cases are no longer hidden, but they also no longer block steady-state frame reuse.
- Visible shell correctness and diagnostics truth are materially better, but first/full-frame cost and long-tail spike reduction still remain active P2 work.

These signals mean fen has closed the P0 frame-pipeline blockers, but it is not yet performance-closed overall.

All work below is guided by the FenBrowser mandate:
- Security is first-class.
- Modularity is first-class.
- Spec compliance is first-class.
- Performance is first-class.
- Logging and diagnostics are first-class.
- We do not repeat the structural mistakes of Chromium, Firefox, or WebKit.
- Architecture is destiny.

## Rendering/Performance Non-Negotiables
- No fake performance wins that lower rendering correctness.
- No permanent reliance on full-frame raster for ordinary steady-state updates.
- No hidden layout fallback that silently rewrites bad constraints into viewport-sized values without explicit diagnostics and ownership.
- No unbounded caches for glyphs, images, display lists, paint nodes, or decoded assets.
- No UI-thread rendering creep. UI thread owns widgets and windowing; render thread owns layout, paint, raster work.
- No logging blind spots on hot-path regressions. Frame cost must be attributable by stage and by invalidation reason.
- No library dominance. Skia remains a drawing backend, not the owner of browser layout metrics or scene semantics.
- No benchmark-only tuning that regresses interactive behavior, determinism, or memory safety.

## Required Acceptance Gates For Every Work Item
- Correctness: visible output and DOM-to-box behavior remain accurate.
- Performance: frame-time target, allocation target, or throughput target is explicit.
- Observability: structured logs, counters, and failure reasons exist.
- Determinism: same invalidation input produces the same render decision.
- Memory: ownership, pooling, and eviction behavior are defined.
- Recovery: over-budget and failure paths degrade visibly and safely, not silently.
- Scale: the chosen approach still works for large documents, image-heavy pages, and repeated interaction.

## Anti-Mistakes We Must Avoid
- No global "mark everything dirty" fallback becoming permanent architecture.
- No damage-raster feature that is nominal in code but disabled in real production paths.
- No duplicate render decision logic split between host, browser API, and renderer internals.
- No repeated full DOM/layout/paint work for unchanged frames just because a timer fired.
- No caching layer without invalidation truth.
- No text measurement shortcuts that make Latin pages look acceptable while silently failing broader script coverage.
- No overfitting to Google-only behavior; use Google as a stress signal, not as the spec.

## Execution Order
1. P0: frame-pipeline and steady-state blockers.
2. P1: subsystem completion and measurable optimization.
3. P2: production hardening, budgets, and long-tail scale discipline.

## P0 Closure Evidence
P0 closed on `2026-03-30` with code, focused regressions, a full solution build, and a required clean-state host cycle.

- Stabilize invalidation and frame ownership:
  - `BrowserIntegration` now records frames through `RenderFrameRequest` instead of a raw renderer call.
  - host-side requests now carry explicit invalidation reasons and request sources, and committed-frame telemetry is written to structured `[FRAME] Commit` entries.
- Make damage tracking and reusable base frames the real update path:
  - the host now seeds reusable base frames when policy allows it.
  - the renderer now preserves the seeded base frame when a steady-state frame has no remaining damage, and the live host run showed repeated `PreservedBaseFrame` commits at `0.07ms` to `0.19ms`.
- Fix layout constraint propagation and sanitize-at-source behavior:
  - block, inline, flex, and grid width resolution now route through a shared source-order resolver.
  - layout diagnostics now expose whether width came from `available`, `containing-block`, `viewport`, or the emergency fallback instead of rewriting constraints silently deep in the pipeline.
- Make render telemetry first-class and cheap:
  - `RenderFrameResult` now carries invalidation reason, caller identity, raster mode, and per-stage telemetry.
  - meaningful frame telemetry is mirrored into both text logs and structured `.jsonl` output for regression and operator review.

Verification that closed P0:
- `dotnet build FenBrowser.sln -c Debug -v minimal -nologo`: pass
- `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj -c Debug -v minimal -nologo --no-build --filter "FullyQualifiedName~RenderFrameTelemetryTests|FullyQualifiedName~RenderWatchdogTests|FullyQualifiedName~LayoutConstraintResolverTests"`: pass (`7/7`)
- Required host runtime cycle on `2026-03-30` emitted:
  - `debug_screenshot.png`
  - `dom_dump.txt`
  - `logs/raw_source_20260330_123307.html`
  - `logs/engine_source_20260330_123329.html`
  - `logs/rendered_text_20260330_123329.txt`
  - `logs/fenbrowser_20260330_123306.log`
  - `logs/fenbrowser_20260330_123306.jsonl`

## P1 Closure Evidence
P1 closed on `2026-03-30` with code, focused regressions, a full solution build, and a required clean-state host cycle.

- Reuse layout, paint-tree, and frame work aggressively:
  - `SkiaDomRenderer` now keeps paint-only invalidation out of layout on the hot path.
  - committed frame telemetry proves converged animation frames are `layoutUpdated=false` while still rebuilding paint output as needed.
  - steady-state animation frames now run through `Damage` rasterization instead of defaulting back to full-frame work.
- Optimize restyle invalidation where it matters on the live path:
  - `CssAnimationEngine.DetermineInvalidationKind(...)` now classifies paint-only animation properties separately from geometry-changing properties.
  - opacity-only animation churn no longer forces layout in the regression slice.
- Harden typography and shaping throughput:
  - `SkiaTextMeasurer` caches stable width and line-height inputs.
  - `SkiaFontService` reuses metrics, width, and glyph-run results for repeated text inputs.
- Bound image decode and relayout churn:
  - `ImageLoader.PrewarmImageAsync(...)` now batches burst relayout signals instead of emitting one relayout per decoded image.
  - host repaint and relayout callbacks now use explicit active-DOM semantics.
- Reduce verification-to-runtime mismatch:
  - `ContentVerifier` now resets per navigation and accepts authoritative top-level source/rendered registrations only.
  - `ResourceManager` only registers top-level document fetches as authoritative source truth.
  - rendered-text capture now includes visible control fallback text without duplicating aria-label fallback when visible text already exists.
  - deep debug defaults are now off, while frame timing and verification logging stay on; per-text layout logs are gated behind layout-debug flags so observability no longer distorts the hot path.

Verification that closed P1:
- `dotnet build FenBrowser.sln -c Debug -v minimal -nologo`: pass
- `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj -c Debug -v minimal -nologo --no-build --filter "FullyQualifiedName~RenderFrameTelemetryTests|FullyQualifiedName~BrowserHostRenderedTextTests|FullyQualifiedName~BrowserHostImageInvalidationTests|FullyQualifiedName~TypographyCachingTests|FullyQualifiedName~GoogleSnapshotDiagnosticsTests|FullyQualifiedName~LayoutConstraintResolverTests|FullyQualifiedName~ContentVerifierStateTests"`: pass (`17/17`)
- Required host runtime cycle on `2026-03-30` emitted:
  - `debug_screenshot.png`
  - `dom_dump.txt`
  - `logs/raw_source_20260330_131408.html`
  - `logs/engine_source_20260330_131429.html`
  - `logs/rendered_text_20260330_131429.txt`
  - `logs/fenbrowser_20260330_131407.log`
  - `logs/fenbrowser_20260330_131407.jsonl`
- Runtime proof that closed P1:
  - first navigation commit remained expensive at `557.24ms`, which keeps P2 open.
  - the converged animation tail settled to `2.64ms` to `3.15ms` damage-raster frames with `layoutUpdated=false` and `watchdogTriggered=false`, which is the production-critical P1 turning point.

## P0 Workstreams
| Priority | Workstream | Scope | Production-Grade Outcome |
|---|---|---|---|
| P0 | Stabilize invalidation and frame ownership | `FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs`, `BrowserApi.cs`, `BrowserEngine.cs`, `Rendering/Core/RenderPipeline.cs`, `RenderFrameResult.cs`, `FenBrowser.Host/BrowserIntegration.cs`, `FenBrowser.Host/ProcessIsolation/*` | Completed on `2026-03-30`. A frame is only re-laid out, repainted, or re-rastered when a real invalidation reason exists. Host, browser API, and renderer agree on who requested the frame and why. |
| P0 | Make damage tracking and reusable base frames the real update path | `FenBrowser.FenEngine/Rendering/Compositing/*`, `PaintDamageTracker.cs`, `DamageRasterizationPolicy.cs`, `SkiaRenderer.cs`, host frame-buffer ownership paths | Completed on `2026-03-30`. Partial updates now act as the default steady-state path when valid. Base-frame seeding, damage region bounds, and fallback behavior are explicit and safe. Emergency full raster remains recovery-only. |
| P0 | Fix layout constraint propagation and sanitize-at-source behavior | `FenBrowser.FenEngine/Layout/*`, especially `MinimalLayoutComputer.cs`, `LayoutContext.cs`, `ContainingBlockResolver.cs`, `BoxModel.cs`, `LayoutValidator.cs`, block/inline/flex/grid helpers | Completed on `2026-03-30` for source-side ownership and diagnostics. Layout inputs now declare where width came from before reaching deep algorithms. Remaining `Raw=0` viewport fallback cases move to P1 cleanup rather than blocking P0. |
| P0 | Make render telemetry first-class and cheap | `FenBrowser.Core/Logging/*`, `FenBrowser.Core/Verification/ContentVerifier.cs`, `FenBrowser.FenEngine/Rendering/*`, `FenBrowser.Host/BrowserIntegration.cs` | Completed on `2026-03-30`. Every committed frame now reports URL, invalidation reason, DOM/box/paint counts, stage timings, damage area, raster mode, and watchdog outcomes in structured logs that can drive regression gates. |

## P1 Workstreams
| Priority | Workstream | Scope | Production-Grade Outcome |
|---|---|---|---|
| P1 | Reuse layout, paint-tree, and display-list work aggressively | `FenBrowser.FenEngine/Rendering/PaintTree/*`, `RenderTree/*`, `ElementStateManager.cs`, `SkiaDomRenderer.cs`, layout-to-paint bridge surfaces | Completed on `2026-03-30`. Paint-only invalidation now avoids relayout on the hot path, and converged animation frames reuse damage/base-frame state instead of falling back to repeated full work. |
| P1 | Optimize CSS/style matching and restyle invalidation | `FenBrowser.FenEngine/Rendering/Css/*`, `FenBrowser.Core/Css/*`, selector matching and style applicator paths | Completed on `2026-03-30` for the live render hot path. Animation-driven restyle invalidation now distinguishes paint-only properties from geometry-changing properties so opacity-class churn no longer triggers relayout by default. |
| P1 | Harden typography, glyph caching, and text shaping throughput | `FenBrowser.FenEngine/Typography/*`, `Adapters/SkiaTextMeasurer.cs`, `Rendering/PaintTree/PositionedGlyph.cs`, font/fallback services | Completed on `2026-03-30`. Stable measurement and shaping inputs now hit bounded caches for line heights, widths, metrics, and glyph runs. |
| P1 | Bound image decode, animated image churn, and raster-side asset cost | `FenBrowser.FenEngine/Rendering/ImageLoader.cs`, image cache/decode paths, animation invalidation logic | Completed on `2026-03-30`. Burst image prewarm no longer emits one relayout per decode, and image-driven repaint/relayout signals now route through explicit host semantics. |
| P1 | Reduce verification-to-runtime mismatch in visual/text fidelity | `FenBrowser.Core/Verification/ContentVerifier.cs`, `FenBrowser.Tests/Core/GoogleSnapshotDiagnosticsTests.cs`, browser diagnostics capture seams | Completed on `2026-03-30`. Verification now reports authoritative top-level source/rendered truth instead of whichever subresource finished last, and rendered-text capture better reflects visible control text. |
| P1 | Remove repeated steady-state work after first meaningful paint | navigation lifecycle, repaint scheduling, timer-driven invalidation, overlay/input repaint paths | Completed on `2026-03-30`. After convergence, ordinary animation frames now settle into low-single-digit millisecond damage-raster commits instead of repeated full-page render work. |

## P2 Workstreams
| Priority | Workstream | Scope | Production-Grade Outcome |
|---|---|---|---|
| P2 | Enforce memory budgets and eviction policy across render assets | glyph caches, decoded image caches, reusable frame buffers, paint/display caches, `ShardedCache<T>` consumers | Renderer memory usage becomes bounded, observable, and eviction-safe under long sessions and large pages. |
| P2 | Add interaction jank control and deadline-aware scheduling | input-to-render path, `EventLoopCoordinator`, render-thread queues, overlay updates, scroll/input hot paths | Input, scroll, typing, and focus transitions stay responsive even while pages are busy. Deadline misses degrade gracefully instead of cascading. |
| P2 | Build a production benchmark and regression gate suite | `FenBrowser.Tests/Rendering/*`, `FenBrowser.Tooling/*` benchmark harnesses, representative page/profile corpus | Fen gains stable render/perf benchmarks with thresholds for DOM size, first meaningful paint, steady-state frame cost, text throughput, image churn, and memory. |
| P2 | Harden platform abstraction for future GPU/backend evolution | backend abstraction seams, host compositor contract, raster/present boundaries | Render/perf improvements do not lock fen into one OS or one backend strategy. The architecture stays portable while getting faster. |

## Coverage Map

### FenBrowser.FenEngine
- Frame ownership, damage tracking, paint/raster cost:
  `Rendering/SkiaDomRenderer.cs`, `Rendering/SkiaRenderer.cs`, `Rendering/Core/*`, `Rendering/Compositing/*`, `Rendering/PaintTree/*`, `Rendering/RenderTree/*`
- Layout constraint correctness and relayout cost:
  `Layout/*`
- Typography and text throughput:
  `Typography/*`, `Adapters/SkiaTextMeasurer.cs`
- Image decode and asset churn:
  `Rendering/ImageLoader.cs`, related asset caches and animation paths

### FenBrowser.Core
- Logging and telemetry:
  `Logging/*`, `FenLogger.cs`, `Verification/ContentVerifier.cs`
- Shared cache and budget primitives:
  `Cache/*`, `System/FrameDeadline.cs`, supporting thin contracts used by render paths

### FenBrowser.Host
- Host/render contract and presentation ownership:
  `BrowserIntegration.cs`, `ProcessIsolation/*`, widget repaint/invalidation seams
- Debug and operator evidence routing:
  root artifact and log ownership, frame record surfaces

## Closure Rules For This Ledger
- P0 closed on `2026-03-30` because fen now maintains a believable steady-state frame model under a real Google-class page instead of redoing full render work for every timer/animation frame.
- P1 closed on `2026-03-30` because renderer subsystems now show measurable reuse and bounded steady-state cost under the live Google-class repro instead of only cleaner code.
- P2 closes only when performance discipline is enforced by budgets, benchmarks, and memory policy rather than by developer intention.

## Initial Reality-Based Success Criteria
The first serious definition of success for this ledger is:

- Google-class pages stay visibly correct.
- Steady-state no longer repeatedly pays full layout + paint + raster cost.
- Watchdog warnings become rare exceptions, not expected steady-state output.
- Damage-raster and reusable-frame paths are real production paths.
- Frame logs explain exactly why a frame was expensive.
- Text, images, and overlays stop causing disproportionate churn.

Until those statements are true, fen is still in rendering/performance closure mode.
