# FenBrowser — Next Tasks

> Auto-generated from GATE 0 audit + diagnostic infrastructure survey.
> Last refreshed: 2026-06-30.
> Only dependency-ready tasks are listed. Tasks are ordered by priority.

## GATE Status Summary

| Gate | Name | Status | Blocker |
|------|------|--------|---------|
| 0 | Reality Audit | **95%** | NEXT_TASKS.md (this file) |
| 1 | Diagnostic Spine | **85%** | ipc.json, sandbox_denials.json, performance.json artifacts |
| 2 | Architecture Freeze | **0%** | Gates 0-1 completion |
| 3 | Build/Test/Trace Infra | **80%** | Clean build OK; unit tests OK; test262 OK; WPT baseline needed |
| 4 | Real-Site Boot Pipeline | **10%** | **IMMEDIATE PRIORITY** |

## Current Real-Site State (from latest trace bundles)

| Site | DOM | Styles | Scripts Exec | Screenshot | Verdict |
|------|-----|--------|-------------|------------|---------|
| example.com | 18 nodes | 12/12 styled | 0 scripts | Captured | Perfect |
| news.ycombinator.com | 816 nodes | 777/816 styled | few | Captured | 95% layout coverage |
| react.dev | 1843 nodes | 1240/1843 styled | unknown | Partial | 67% layout coverage |
| github.com | 2891 nodes | 1920/2891 styled | 69/84 scripts | Captured | 1418 layout boxes; 63 zero-area boxes; header height/top alignment and logo/menu allocation fixed; nav labels no longer self-overlap; hero headline/description wrap inside max-width caps; dropdown/background and visual sizing remain |
| x.com | 81 nodes | 10/81 styled | Very few | Mostly white | 12% layout; scripts fail early |

## Priority 1 — Real-Site Blocker Diagnosis

### Task T4.1 — Fresh GitHub trace with latest debug-site

- **Task ID**: T4.1
- **Title**: Run debug-site on GitHub with latest tooling
- **Area**: Diagnostic spine + real-site
- **Status**: COMPLETED
- **Priority**: 1
- **Risk Level**: Low
- **Dependencies**: None (debug-site command exists and builds)
- **Reproduction**: Run FenBrowser.Tooling debug-site https://github.com 20000
- **Expected**: Fresh trace with full artifact bundle; identify first fatal JS exception, first missing API, DCL/load status, layout blocker detail
- **Evidence**: `logs/real-site/github.com/20260627T163931Z/`
- **Result**: Default-deadline run captures style/layout/paint/raster: 1415 layout boxes, 733 paint nodes, 193 zero-area boxes, DCL/load counters true, first missing API `Document.tagName`, 7 script execution failures.

### Task T4.2 — Fix GitHub zero-area layout boxes

- **Task ID**: T4.2
- **Title**: Fix inline/flex child zero-height layout for GitHub
- **Area**: Layout engine (FenBrowser.FenEngine)
- **Status**: IN_PROGRESS
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1 (fresh diagnosis)
- **Files**: FenBrowser.FenEngine/Layout/Contexts/BlockFormattingContext.cs, FenBrowser.FenEngine/Layout/Contexts/FlexFormattingContext.cs, FenBrowser.FenEngine/Layout/Contexts/InlineFormattingContext.cs, FenBrowser.FenEngine/Layout/LayoutStyleResolver.cs, FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs
- **Current**: GitHub captures layout but still has 63 zero-area boxes after unitless `line-height` flex sizing, repeated percentage-height normalization, auto-height positioned-header percentage sizing, flex/grid item blockification, shrink-to-fit text/row-flex intrinsic width preservation, leaf/control row-flex shrink floors, inline max-width line limits, `width:auto` flex intrinsic probing, and position-only `background` shorthand reset were fixed. `height:100%` under an auto-height positioned header no longer resolves against viewport/out-of-flow used geometry; the header is now 60px and top-aligned. Column-flex hero text wraps inside its resolved max-width cap. The logo/menu split now starts from a shrink-wrapped logo shell instead of a half-header stale `width:100%` basis, giving search/sign-in/sign-up visible allocation. GitHub's dropdown trigger buttons now keep their authored transparent background instead of inheriting the UA light-gray button fill. Remaining visual issues are sign-in content-box sizing, hidden dropdown max-content geometry, and hero visual/carousel sizing, not header viewport-height inflation, nav labels shrinking below their own text, search/sign-in overlap from the logo shell, white dropdown trigger boxes, or the hero headline overflowing as one unwrapped line.
- **Expected**: Remaining zero-area boxes should be traced to the next width/placement/intrinsic-sizing root cause, not unresolved percentage heights resolving against viewport height or auto-height positioned headers.
- **Tests required**: Layout unit tests; WPT css-flexbox tests
- **Evidence**: Before/after layout dumps showing first `NavGroup` title height moved from ~1.5px to 18px and parent group height moved from 800px to 381px; header-specific bundles `logs/real-site/github.com/20260630T052935Z/` and `logs/real-site/github.com/20260630T053453Z/` show the marketing header moving from `1280x832` with nav around `y=402` to `1280x60` with nav at `y=16`. Bundle `logs/real-site/github.com/20260630T061451Z/` keeps the header top-aligned, reduces zero-area boxes to 64, and shows nav leaf widths preserving visible text (`Platform` button `81.0px` for `60.1px` text; `Open Source` button `112.0px` for `91.5px` text; `Pricing` link `56.0px` for `55.8px` text). The hero overflow repro in `logs/real-site/github.com/20260630T063502Z/` had the H1 at `924.0x69.1` while its text line measured `1135.1px`; after inline max-width line-limit handling, `logs/real-site/github.com/20260630T064734Z/` lays out the H1 as a centered `891.1x138.2` two-line box and the paragraph as `518.6x54.0`. After `width:auto` flex intrinsic probing and keyword projection clearing, `logs/real-site/github.com/20260630T070038Z/` reduces zero-area boxes to 63 and changes the header logo shell from the prior `608.0px` half-row basis to `54.7px`, with the menu/search group starting at `x=86.7` and search widening to `320.0px`. After `background: 0 0` shorthand expansion now resets `background-color` to transparent, `logs/real-site/github.com/20260630T071253Z/` keeps 63 zero-area boxes, changes the five `.NavDropdown-module__button__PEHWX` styles from `background=#F8F9FA` to `background=#00FFFFFF`, and removes the prior light-gray `BackgroundPaintNode` entries for those buttons.

### Task T4.3 — Fix GitHub script execution failures

- **Task ID**: T4.3
- **Title**: Diagnose and fix failed scripts on GitHub (7 of 84)
- **Area**: Script loading / JS engine integration
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1 (fresh diagnosis)
- **Files**: FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs
- **Current**: 69/84 scripts execute; 7 fail. Failures include undefined `.replace`, missing `document.head.prepend(...)`, null `.readyState`, and parser `Unexpected token '/'` cases.
- **Root cause hypothesis**: Missing Web APIs, document/head host-object gaps, module parser gaps, or lifecycle object mismatch.
- **Evidence**: script_loading.json with per-script failure details; missing_apis.json

### Task T4.4 — Fix GitHub lifecycle/readyState consistency

- **Task ID**: T4.4
- **Title**: Fix GitHub lifecycle/readyState consistency
- **Area**: Event loop / document lifecycle
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1, T4.3 (script failures may block DCL)
- **Files**: EventLoopCoordinator.cs, BrowserScriptEngineRuntime.cs
- **Current**: DCL/load counters are true in `event_loop.json`, but the navigation detail and readyState probe still report loading-state fields.
- **Expected**: `event_loop.json`, navigation detail, and `document.readyState` probes agree after parser-inserted scripts execute and parsing completes
- **Evidence**: event_loop.json with DCL/load timestamps

## Priority 2 — Diagnostic Spine Completion

### Task T1.7 — Add ipc.json to trace bundle

- **Status**: NOT_STARTED | **Priority**: 2 | **Dependencies**: None
- Hook into IPC layer to capture message metadata; serialize to bundle

### Task T1.9 — Add performance.json to trace bundle

- **Status**: NOT_STARTED | **Priority**: 2 | **Dependencies**: None
- Export frame timing and allocation data from RenderFrameTelemetry

## Priority 2 — Real-Site Expansion

### Task T4.5 — Run debug-site on react.dev

- **Status**: NOT_STARTED | **Priority**: 2 | **Dependencies**: T4.1 pattern
- Identify why 497/1843 elements (27%) lack layout rects

### Task T4.6 — Run debug-site on x.com

- **Status**: NOT_STARTED | **Priority**: 2 | **Dependencies**: Network fixes may be needed
- Identify first fatal script/network/API failure; only 10/81 elements get layout rects

## Immediate Next Action

**Continue T4.2**: Fix the remaining GitHub zero-area/layout fidelity gaps using the latest bundle:
1. `style_layout.json` - 63 zero-area boxes, no captured layout blocker
2. `layout_dump.txt` - header is `1280x60` and top-aligned; logo shell is shrink-wrapped; hero headline/description wrap inside max-width caps; inspect sign-in content-box sizing, hidden dropdown max-content geometry, and visual/carousel geometry
3. `screenshot.png` - nav labels no longer self-overlap, dropdown trigger boxes are transparent, search/sign-in/sign-up are visibly allocated, and hero text no longer clips; remaining visual acceptance signal is sign-in/content-box and hero visual sizing
4. Add or extend focused regressions before changing the formatting context or cascade path
