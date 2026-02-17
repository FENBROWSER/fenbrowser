# FenBrowser Layout Gaps Analysis - Google.com Rendering

Date: 2026-02-17

## Tier 1 - Critical (open)

*(none newly identified)*

## Tier 2 - High impact (open)

### 1. List layout polish
- `list-style-position: inside` uses a simple offset; markers are not true inline boxes, so wrapping misaligns bullets with text.

### 2. CSS parser coverage
- Media range context (`width >= 500px`) and complex color shorthands in backgrounds are skipped; some responsive/themed styles are dropped.

## Tier 3 - Medium impact (open)

### 3. Scroll snap heuristics
- Snap targets still ignore `scroll-margin` and velocity when choosing proximity; padding is now applied but heuristic may pick adjacent cards in wide carousels.

## Recently fixed (validated in code)

- `text-overflow: ellipsis` clamps overflowing inline runs (`InlineLayoutComputer.FlushLine`).
- `flex-wrap: wrap-reverse` orders lines correctly; flex basis `auto` probes intrinsic widths (`FlexFormattingContext`).
- Scroll snapping honors `scroll-snap-align` and snaps on both axes (`ScrollManager`).
- Counter styles include `lower-greek`, `armenian`, `georgian`; `list-style-image` renders via `ImageLoader` at 1em (`NewPaintTreeBuilder`).
- Ruby vertical-rl annotations swap axes and align with base text (`InlineLayoutComputer`).
- Relative positioning applied; inset and multi shadows render; CSS transforms applied in paint tree.
- Gradient backgrounds, backdrop-filter, placeholder styling wired into active pipeline.
- Grid auto-fit/auto-fill with `minmax()` works; implicit tracks grow; table auto-layout respects `border-spacing` and fits width for separate borders.

## Summary Priority Table

| # | Feature | Impact | Status | File(s) |
|---|---------|--------|--------|---------|
| 1 | text-overflow: ellipsis | **Critical** | Fixed (2026-02-17) | `InlineLayoutComputer.cs` |
| 2 | flex-wrap: wrap-reverse | **Critical** | Fixed (2026-02-17) | `FlexFormattingContext.cs` |
| 3 | Grid auto-fit/auto-fill + minmax | **Critical** | Fixed (2026-02-17) | `GridLayoutComputer.cs` |
| 4 | flex-basis intrinsic sizing | **High** | Fixed (2026-02-17) | `FlexFormattingContext.cs` |
| 5 | list-style-image markers | **High** | Fixed (2026-02-17) | `NewPaintTreeBuilder.cs` |
| 6 | scroll-snap align / X axis | **High** | Fixed (2026-02-17) | `ScrollManager.cs` |
| 7 | flex gaps (row/column gap) | **High** | Fixed (2026-02-17) | `FlexFormattingContext.cs` |
| 8 | grid justify/align-content space-* | **High** | Fixed (2026-02-17) | `GridLayoutComputer.cs` |
| 9 | pseudo-element content parsing | **High** | Fixed (2026-02-17) | `Layout/PseudoBox.cs` |
| 10 | list marker positioning (inside) | **Medium** | Open | Inline layout, marker builder |
| 11 | scroll-snap padding/margin heuristics | **Medium** | Open | `ScrollManager.cs` |
| 12 | CSS parser range/color shorthand | **Medium** | Open | `CssParser.cs`, `CssLoader.cs` |

## Fix Blueprint (actionable)

1. List inside positioning: promote markers to true inline boxes so wrapping aligns markers with text baselines.
2. Scroll snap: incorporate `scroll-margin` and velocity-aware proximity; current padding adjustment is partial.
3. CSS parser: add media range syntax and color shorthand extraction for background/border shorthands.
4. Regression fixtures: capture before/after screenshots for google.com home/results at 1920x1080 and 414x896.

## Reproduction Harness

- Stop any running `fenbrowser` / `FenBrowser.Host`; clear `logs/` with `cleanup_logs.bat`.
- Launch `FenBrowser.Host` at `https://www.google.com` (or `raw_source_google.html`) and wait 25s before collecting artifacts.
- Inspect `debug_screenshot.png`, then `logs/raw_source_*.html`, `dom_dump.txt`, and `logs/fenbrowser_*.log`.
- Use consistent viewport (1920x1080) for before/after diffs.

## Acceptance Tests

- Layout unit tests for flex gaps, grid align-content space-* , pseudo-content parsing, list marker inside wrapping.
- Rendering tests for list-style-image, scroll snap padding/margin, ruby vertical-rl, and table spacing; prefer golden diffs in `FenBrowser.Tests`.
- Performance: keep new gap logic under 100 ms/frame; avoid per-frame allocations of Skia objects.

## Risks and Dependencies

- Flex/grid gap changes alter flow geometry; rerun regression suites.
- Pseudo-content parser touches before/after rendering; ensure no regressions in counters.
- Scroll snap heuristics must avoid scroll jitter; guard with thresholds and debouncing.
