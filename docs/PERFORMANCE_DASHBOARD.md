# FenBrowser Performance Dashboard

Status: RESEARCHED. Snapshot date: 2026-07-14.

Performance work is evidence-driven. This dashboard records available measurements without claiming an optimization target until profiles identify a bottleneck.

## Current Google frame sample

Source: `logs/real-site/www.google.com/20260714T075906Z/style_layout.json`.

| Metric | Result |
| --- | ---: |
| Layout | 56.8748 ms |
| Paint | 256.4934 ms |
| Raster | 101.6828 ms |
| Captured stage total | 419.108 ms |
| Watchdog | Frame exceeded budget during raster; captured duration 424.62 ms versus 16.67 ms budget |

This is one diagnostic frame, not a benchmark distribution. Paint is the largest reported stage, but no CPU/allocation profile in this audit proves its internal cause.

## Required metrics

| Group | Metrics | Status |
| --- | --- | --- |
| Navigation/network | commit, DCL, load, request wait, bytes, failures | INTEGRATED |
| Parser/script | HTML/CSS parse, JS compile/execute, long tasks | STUBBED |
| Rendering | style, Box Tree/layout, Paint Tree, raster, submit, frame interval | INTEGRATED |
| Scale | DOM nodes, CSS rules, layout boxes, paint/display commands | INTEGRATED |
| Managed memory | allocated bytes by stage/frame, Gen0/1/2 count, pause time, heap size | NOT_STARTED |
| FenJS memory | heap/live objects, collections, pause, host-handle count | NOT_STARTED |
| Native memory | Skia surfaces/images/typefaces, shared buffers, handles | NOT_STARTED |
| IPC | messages, encoded bytes, shared-memory bytes, wait/timeout | NOT_STARTED |
| Responsiveness | input latency, timer/rAF drift, hangs | STUBBED |

## Measurement protocol

1. Name the exact URL or local fixture, viewport, build configuration, process mode, settle/interaction sequence, and machine/runtime version.
2. Capture CPU, allocation, GC, stage trace, and correctness artifacts.
3. Record a baseline distribution, not only one run.
4. Change one measured hot path.
5. Repeat the identical protocol and run focused correctness/regression tests.
6. Record before/after values and the mechanism of improvement.

## Optimization queue

| Candidate | Status | Entry evidence required |
| --- | --- | --- |
| Google paint-stage cost | RESEARCHED | CPU profile, paint-node/command attribution, repeatable distribution |
| Raster budget overrun | RESEARCHED | Raster profile, surface size, invalidation/damage evidence |
| CSS parser allocations | IMPLEMENTED | Existing dirty worktree contains user changes; excluded from this audit slice |
| DOM/host bridge allocation | NOT_STARTED | Allocation profile with member/source attribution |
| Event-loop responsiveness | RESEARCHED | Callback-failure attribution and long-task/timer/rAF trace |

No Rust/C++/native rewrite is justified by the current evidence.
