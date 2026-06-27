# FenBrowser — Next Tasks (Dependency-Ready)

> Auto-generated Gate 0 Reality Audit. Last refreshed: 2026-06-27.
> Only tasks whose dependencies are satisfied are listed as ready.
> Tasks are ordered by priority per PLAN.MD gates.

---

## GATE 1 — DIAGNOSTIC SPINE (Active Priority)

Goal: Make every browser failure traceable.

### T1.1 — Implement structured trace event logger
Status: IMPLEMENTED / REGRESSION_PROTECTED

- **Area**: Core / Logging
- **Dependencies**: None (FenLogger exists)
- **Files**: `FenBrowser.Core/Logging/EngineLogContracts.cs`, `FenBrowser.Core/Logging/EngineLogSinks.cs`, `FenBrowser.Core/Logging/EngineLog.cs`
- **Spec**: PLAN.MD § "Diagnostic Spine Requirement"
- **Description**: Implemented a diagnostic JSONL trace sink at the existing `EngineLog` boundary. The sink emits the PLAN.MD fields (`ts`, `level`, `category`, `event`, session/process/navigation/document/frame/realm/script/request/task IDs, `message`, `data`) and preserves event/category override fields for later instrumentation.
- **Verification**: `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj --filter FullyQualifiedName~FenBrowser.Tests.Logging.EngineLogSettingsTests --no-restore -v minimal` passed 3/3.
- **Remaining follow-up**: Browser sessions still need subsystem instrumentation (T1.2-T1.8) to populate complete lifecycle events and write full per-site trace bundles.

### T1.2 — Instrument navigation lifecycle with trace events
Status: IMPLEMENTED / REGRESSION_PROTECTED

- **Area**: Core / Navigation
- **Dependencies**: T1.1
- **Files**: `FenBrowser.Core/Engine/NavigationLifecycle.cs`, `FenBrowser.FenEngine/Rendering/BrowserApi.cs`, `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs`, `FenBrowser.Tests/Core/NavigationLifecycleTraceTests.cs`, `FenBrowser.Tests/Engine/CustomHtmlEngineDocumentTraceTests.cs`
- **Spec**: PLAN.MD § "Diagnostic Spine Requirement" items 1-4
- **Description**: Navigation lifecycle transitions now emit diagnostic trace events for request, fetch start, response received, redirect, commit, interactive, load, failed, and cancelled states. The render path carries the active navigation ID into the parser, and `CustomHtmlEngine` emits `DocumentCreated` with a document ID after the real DOM `Document` is created.
- **Verification**: `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~CustomHtmlEngineDocumentTraceTests|FullyQualifiedName~NavigationLifecycleTraceTests|FullyQualifiedName~EngineLogSettingsTests" --no-restore -v minimal` passed 4/4.
- **Remaining follow-up**: T1.3 must add parser-phase trace events for HTML parsing start/complete and stylesheet/script discovery.

### T1.3 — Instrument HTML parsing with trace events
Status: PARTIAL / REGRESSION_PROTECTED

- **Area**: Core / HTML Parser
- **Dependencies**: T1.1
- **Files**: `FenBrowser.Core/Parsing/HtmlParser.cs`, `FenBrowser.Tests/Core/Parsing/HtmlParserTraceTests.cs`
- **Spec**: PLAN.MD § "Diagnostic Spine Requirement" items 4-6
- **Description**: The canonical document parser now emits `HTMLParsingStarted`, `HTMLParsingCompleted`, and `HTMLParsingFailed` diagnostic trace events with navigation correlation, URL, input length, outcome, token count, and parse timing.
- **Verification**: `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~HtmlParserTraceTests|FullyQualifiedName~CustomHtmlEngineDocumentTraceTests|FullyQualifiedName~NavigationLifecycleTraceTests|FullyQualifiedName~EngineLogSettingsTests" --no-restore -v minimal` passed 5/5.
- **Remaining follow-up**: Stylesheet and script discovery trace events still need parser/preload-scanner instrumentation.

### T1.4 — Instrument script loading with per-script lifecycle trace
- **Area**: FenEngine / Scripting
- **Dependencies**: T1.1
- **Files**: `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs`
- **Spec**: PLAN.MD § "Script Loading Trace Requirements"
- **Description**: Add trace events for: ScriptDiscovered, ScriptFetchStarted, ScriptFetchCompleted, ScriptReady, ScriptExecutionStarted, ScriptExecutionCompleted, ScriptExecutionFailed, DOMContentLoadedBlockedByScript. Per-script fields: script ID, URL, inline/external, classic/module, async, defer, parser-inserted, blocking status, fetch status, MIME type, execution order, exception if failed.
- **Acceptance**: Every script on a page produces a complete lifecycle trace. Script ordering errors are detectable from trace.

### T1.5 — Instrument event loop with task/microtask/timer/rAF trace
- **Area**: FenEngine / Event Loop
- **Dependencies**: T1.1
- **Files**: `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs`, `TaskQueue.cs`, `MicrotaskQueue.cs`
- **Spec**: PLAN.MD § "Event Loop Trace Requirements"
- **Description**: Add trace events for: TaskQueued, TaskStarted, TaskCompleted, MicrotaskQueued, MicrotaskCheckpointStarted, MicrotaskExecuted, MicrotaskCheckpointCompleted, TimerScheduled, TimerFired, RequestAnimationFrameScheduled, RequestAnimationFrameFired, RenderOpportunityStarted, RenderOpportunityCompleted.
- **Acceptance**: Event loop trace detects: promises not running, microtasks running at wrong time, DOMContentLoaded firing early, timers not firing, rAF not firing, render loop not scheduled, endless task loops.

### T1.6 — Implement missing API tracker
- **Area**: FenEngine / Scripting
- **Dependencies**: T1.1
- **Files**: `FenBrowser.FenEngine/Scripting/MissingApiTracker.cs` (new), `BrowserFenJsHostHooks`
- **Spec**: PLAN.MD § "Missing API Tracker"
- **Description**: When JavaScript accesses an unimplemented browser API via IHostHooks, log: API name, object/prototype, site URL, script URL, line/column if available, first-seen trace ID, exception text. Deduplicate by API name. Output `missing_apis.json` per site.
- **Acceptance**: Missing API tracker output. Common framework boot failures traceable to specific missing APIs.

### T1.7 — Implement debug-site command with trace bundle output
- **Area**: Host / Tooling
- **Dependencies**: T1.1-T1.6
- **Files**: `FenBrowser.Tooling/Program.cs`, `FenBrowser.Host/BrowserIntegration.cs`
- **Spec**: PLAN.MD § "Style/Layout/Paint Debugging" + "Diagnostic Spine Requirement"
- **Description**: Add `fenbrowser --debug-site <url> --trace all --output <folder>` command that produces a complete trace bundle: summary.md, trace.jsonl, console.log, network.json, exceptions.json, missing_apis.json, script_loading.json, event_loop.json, style_layout.json, dom_dump.html, style_dump.txt, layout_dump.txt, paint_dump.txt, display_list.txt, screenshot.png.
- **Acceptance**: A single command produces a complete trace bundle for any URL. Agents can debug from artifacts without guessing.

### T1.8 — Implement element inspection debug commands
- **Area**: FenEngine / Rendering
- **Dependencies**: T1.1
- **Files**: `FenBrowser.Tooling/Program.cs`, `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
- **Spec**: PLAN.MD § "Style/Layout/Paint Debugging"
- **Description**: Add commands: `--dump-dom <url>`, `--dump-style <url>`, `--dump-layout <url>`, `--dump-paint <url>`, `--dump-display-list <url>`, `--screenshot <url>`, `--inspect-selector <url> "<selector>"`. Element inspection must show: name, id/classes, computed display/position, width/height, margin/padding/border, matched rules, winning rule per property, layout dirty reason, paint status, why-not-visible if hidden/zero-size/clipped.
- **Acceptance**: Debug commands produce actionable output. Element inspection explains why an element is not visible.

---

## GATE 2 — ARCHITECTURE FREEZE

All architecture documents are DESIGN_INTENT. Implementation is subsequent gates.

### T2.1 — Write ARCHITECTURE.md
- **Area**: Docs
- **Dependencies**: ENGINE_STATE.md
- **Files**: `docs/ARCHITECTURE.md` (new/update)
- **Description**: Document every major subsystem boundary, owner, contract. Based on ENGINE_STATE.md inventory.

### T2.2 — Write PROCESS_MODEL.md
- **Area**: Docs
- **Dependencies**: ENGINE_STATE.md
- **Files**: `docs/PROCESS_MODEL.md` (new)
- **Description**: UI/renderer/broker/worker/compositor process design.

### T2.3 — Write IPC_MODEL.md
- **Area**: Docs
- **Dependencies**: PROCESS_MODEL.md
- **Files**: `docs/IPC_MODEL.md` (new)
- **Description**: IPC messages, schemas, validation, sync/async policy.

### T2.4 — Write SECURITY_MODEL.md
- **Area**: Docs
- **Dependencies**: PROCESS_MODEL.md
- **Files**: `docs/SECURITY_MODEL.md` (new)
- **Description**: Sandboxing, origin isolation, privileges, brokered access.

### T2.5 — Write MEMORY_MODEL.md
- **Area**: Docs
- **Dependencies**: None
- **Files**: `docs/MEMORY_MODEL.md` (new)
- **Description**: C# GC, JS runtime, DOM lifetimes, native handles, wrapper identity, weak references, object ownership. Required before expanding JS/DOM bindings.

---

## GATE 4 — REAL-SITE BOOT PIPELINE

### T4.1 — Fix first fatal x.com blocker
- **Area**: Scripting / Network
- **Dependencies**: T1.4 (script loading trace)
- **Description**: Diagnose why x.com only produces 10 layout rects from 81 elements. Likely: external script bundles from abs.twimg.com not fetched/executed. Use script loading trace to identify exact failure point.
- **Acceptance**: x.com loads more than 50 elements with layout rects.

### T4.2 — Fix inline/flex zero-height element issue
- **Area**: Layout
- **Dependencies**: T1.8 (element inspection)
- **Description**: GitHub sidebar shows 200px-wide spans with 0px height despite containing text. Diagnose with inline inspection. Likely: inline formatting context height calculation, or flex child measure pass.
- **Acceptance**: GitHub sidebar nav items get non-zero heights.

### T4.3 — Fix GitHub missing layout rects (588 elements)
- **Area**: Layout / Rendering
- **Dependencies**: T1.8, T4.2
- **Description**: 588/1959 elements on GitHub have no layout rect. Classify: display:none (281 legit), offscreen (691), vs genuinely broken (remainder). Fix the genuinely broken category.
- **Acceptance**: GitHub layout rect coverage improves to >80%.

---

## QUICK WINS (Low risk, immediate impact)

These have no dependencies and can be done in any order:

### QW1 — Clean up obsolete API usage in tests
- **Area**: Tests
- **Files**: `FenBrowser.Tests/Core/Parsing/TableParsingTests.cs`, `GoogleSnapshotDiagnosticsTests.cs`, `LayoutStabilityTests.cs`, `FlexDistributionTests.cs`
- **Description**: Replace deprecated `Node.Text`→`TextContent`, `Node.ComputedStyle`→`GetComputedStyle()`, `Element.Attr`→`GetAttribute()`. Removes 13 build warnings.
- **Acceptance**: Zero CS0618 warnings in test build.

### QW2 — Fix xUnit analyzer warnings in tests
- **Area**: Tests
- **Files**: `FenBrowser.Tests/Core/Parsing/StreamingHtmlParserTests.cs`, `AfterHeadParsingTests.cs`
- **Description**: Replace `Assert.False(collection.Contains(x))` → `Assert.DoesNotContain()`, `collection.Where().Single()` → `Assert.Single(filter)`.
- **Acceptance**: Zero xUnit2012/xUnit2031 warnings.

### QW3 — Run WPT dom/ category baseline
- **Area**: Conformance
- **Dependencies**: WebDriver server running
- **Description**: Run full WPT dom/ category via wptrunner to establish DOM baseline beyond the known dom/lists = 95.2%.
- **Acceptance**: WPT dom/ pass rate documented in TEST_BASELINE.md.

### QW4 — Run html5lib conformance baseline
- **Area**: Conformance
- **Dependencies**: None (test data in tree at html5lib-tests/)
- **Description**: Run the html5lib test suite against the HTML parser.
- **Acceptance**: html5lib pass rate documented in TEST_BASELINE.md.
