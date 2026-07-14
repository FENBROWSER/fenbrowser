# FenBrowser CSS, Layout, Paint, and Compositing Tracker

Status: TESTED for the current Google render path. Snapshot date: 2026-07-14.

## Fresh real-site evidence

Bundle: `logs/real-site/www.google.com/20260714T075906Z/`.

| Signal | Result |
| --- | --- |
| DOM nodes | 572 |
| Styled nodes | 446 |
| Layout boxes | 172 |
| Zero-area boxes | 31 |
| Paint roots / nodes | 68 / 93 |
| Raster mode | Full |
| Visible result | Google logo, search control, buttons, links, and footer rendered |
| Captured blocker | No layout or paint blocker |

## Capability view

| Stage | Status | Current evidence | Next falsifiable proof |
| --- | --- | --- | --- |
| CSS tokenization/parsing | TESTED | Real-site styles parsed; focused work exists | Selected CSS parser tests and allocation benchmark |
| Selector matching/specificity/cascade | INTEGRATED | 446 styled nodes | Selector inspection plus WPT cascade slices |
| Computed values/custom properties/media | INTEGRATED | Style dump and real-site boot | Selected WPT and property-by-property inspection |
| Style invalidation | IMPLEMENTED | Runtime invalidation paths exist | Mutation-to-dirty-reason trace and incremental tests |
| Block/inline layout | TESTED | Google main UI rendered | Local reductions and screenshot comparisons |
| Flex/grid/table | INTEGRATED | Implementations exist and are exercised by real pages | Format-specific WPT categories and reductions |
| Positioned/overflow/scroll/transform | INTEGRATED | Source and site use exist | Input/scroll screenshots and selected WPT |
| Text/font/image measurement | INTEGRATED | Skia/HarfBuzz-backed rendering | Font-load/fallback/hostile-resource tests |
| Paint order/stacking/clip/opacity | INTEGRATED | Paint Tree and screenshot captured | Visual reference cases and display-list assertions |
| Damage/frame scheduling/compositing | INTEGRATED | Frame produced | rAF/invalidation/damage and brokered GPU recovery tests |

## Debug contract

Every visible failure must retain screenshot, DOM, style, Box Tree/layout, Paint Tree/display-list, viewport, font/image failures, matched/winning rules for the owning element, dirty reason, paint status, and why an element is hidden, zero-sized, clipped, or offscreen.

The current bundle has dumps, but standalone `--dump-*` and `--inspect-selector` CLI commands remain NOT_STARTED.

## Priority rule

Start with layout only when navigation, document, required resources, script boot, and event-loop milestones succeeded and evidence points to wrong style/geometry/paint. The current Google acceptance gap is automated input/submit and callback attribution, not a confirmed layout blocker.
