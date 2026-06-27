# FenBrowser — Next Tasks

> Auto-generated from GATE 0 audit + diagnostic infrastructure survey.
> Last refreshed: 2026-06-27.
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
| github.com | 2891 nodes | 1920/2891 styled | 69/84 scripts | Captured | 56% layout; 102 zero-area boxes |
| x.com | 81 nodes | 10/81 styled | Very few | Mostly white | 12% layout; scripts fail early |

## Priority 1 — Real-Site Blocker Diagnosis

### Task T4.1 — Fresh GitHub trace with latest debug-site

- **Task ID**: T4.1
- **Title**: Run debug-site on GitHub with latest tooling
- **Area**: Diagnostic spine + real-site
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Low
- **Dependencies**: None (debug-site command exists and builds)
- **Reproduction**: Run FenBrowser.Tooling debug-site https://github.com 20000
- **Expected**: Fresh trace with full artifact bundle; identify first fatal JS exception, first missing API, DCL/load status, layout blocker detail
- **Evidence required**: Full trace bundle in logs/real-site/github.com/

### Task T4.2 — Fix GitHub zero-area layout boxes

- **Task ID**: T4.2
- **Title**: Fix inline/flex child zero-height layout for GitHub
- **Area**: Layout engine (FenBrowser.FenEngine)
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1 (fresh diagnosis)
- **Files**: FenBrowser.FenEngine/Layout/InlineFormattingContext.cs, FlexFormattingContext.cs
- **Current**: GitHub sidebar nav spans 200px wide x 0px tall; 102 elements with text content get zero height
- **Expected**: Inline/inline-flex children with text content have non-zero height from font metrics and line height
- **Tests required**: Layout unit tests; WPT css-flexbox tests
- **Evidence**: Before/after layout dumps showing non-zero heights

### Task T4.3 — Fix GitHub script execution failures

- **Task ID**: T4.3
- **Title**: Diagnose and fix failed scripts on GitHub (7 of 84)
- **Area**: Script loading / JS engine integration
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1 (fresh diagnosis)
- **Files**: FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs
- **Current**: 69/84 scripts execute; 7 fail for unknown reasons
- **Root cause hypothesis**: Missing Web APIs, module gaps, or CORS/network issues
- **Evidence**: script_loading.json with per-script failure details; missing_apis.json

### Task T4.4 — Fix DCL/load event timing on GitHub

- **Task ID**: T4.4
- **Title**: Ensure DOMContentLoaded fires correctly on GitHub
- **Area**: Event loop / document lifecycle
- **Status**: NOT_STARTED
- **Priority**: 1
- **Risk Level**: Medium
- **Dependencies**: T4.1, T4.3 (script failures may block DCL)
- **Files**: EventLoopCoordinator.cs, BrowserScriptEngineRuntime.cs
- **Current**: document.readyState probe returned "loading" in prior trace
- **Expected**: DCL fires after parser-inserted scripts execute and parsing completes
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

**Run T4.1**: Fresh debug-site on GitHub with the latest tooling, then triage:
1. script_loading.json — what causes the 7 script failures?
2. missing_apis.json — what APIs does GitHub JS depend on that are missing?
3. event_loop.json — does DCL/load fire?
4. style_layout.json — zero-area box details
5. exceptions.json — first fatal JS errors
6. Classify first fatal blocker per PLAN.MD bucket system
