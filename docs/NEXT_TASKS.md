# FenBrowser Dependency-Ready Next Tasks

Snapshot date: 2026-07-15. Only tasks whose current dependencies are satisfied are listed. Order follows the diagnostic-first mission; it is not a calendar plan.

## Task TRACE-001

Task ID: TRACE-001
Title: Preserve callback failures and drain logs before bundle export
Area: Diagnostic spine / event loop / Tooling
Owner Agent: Diagnostic Agent
Status: TESTED
Priority: 1
Risk Level: Medium
Dependencies: Current event-loop snapshot, structured logger, and `debug-site` bundle writer are INTEGRATED
Files likely involved: `FenBrowser.FenEngine/Rendering/EventLoopCoordinator.cs`, event-loop diagnostic record types, `FenBrowser.Core/Logging/EngineLog.cs`, `FenBrowser.Tooling/Program.cs`, included diagnostic test surface
Specs/references: HTML event loops; `docs/SPEC_EVENT_LOOP.md`; `docs/DIAGNOSTICS.md`
Current behavior: Timer and event-listener throws plus unhandled Promise rejections retain bounded task, callback, source, receiver, exception, JS-stack, and host-stack fields. Promise reporting waits until the microtask checkpoint, so a same-turn handler suppresses a false failure, and multiple rejections retain observation order. Tooling drains accepted log records before copying artifacts, and callback counts share one event-loop snapshot across `event_loop.json`, `exceptions.json`, trace, and summary. Both attributed Google host-object clusters are fixed; the current bundle reports zero callback failures and zero exceptions.
Expected behavior: Every failed timer/task/event/promise callback has timestamp, task/callback ID, error type/message/stack, realm/script/source where known, and appears before bundle finalization.
Reproduction: Run the local callback-failure fixture and `debug-site https://www.google.com 20000`; compare `event_loop.json`, `exceptions.json`, `trace.jsonl`, and summary counts.
Root cause: The old event-loop snapshot retained only aggregate failure state, function-owned source provenance was lost after top-level execution, and Tooling copied asynchronous logs without a drain boundary.
Implementation plan: Add a bounded typed failure record; propagate it at the callback catch point; add an explicit asynchronous logger drain/export barrier; serialize typed exceptions; preserve behavior if diagnostic export fails.
Tests required: Add microtask throw, callback after navigation invalidation, repeating timer, bounded record/message/stack, redaction, and export-failure isolation to the compiled timer/event/Promise/drain/bundle coverage.
Evidence required: Before bundle with eight unattributed failures; after fixture and real-site bundle with matching counts and source/task identity.
Security impact: Redact page secrets and cap error/stack sizes; logging must not alter exception propagation.
Performance impact: Measure added record allocations and drain duration; no synchronous logging in callback hot paths.
Compatibility impact: Diagnostic-only output change with versioned/additive JSON fields.
Known risks: Flush deadlock, reordered events, or retaining callback/realm graphs through diagnostics.
Blockers: None
Next action: Expose the green local interaction sequence through Tooling, then automate Google focus/type/submit with screenshot, trace, and request/navigation evidence.

## Task TRACE-002

Task ID: TRACE-002
Title: Export rich missing-API records and reject false positives
Area: WebIDL / DOM diagnostics
Owner Agent: Bindings Diagnostic Agent
Status: RESEARCHED
Priority: 1
Risk Level: Medium
Dependencies: Runtime `MissingApiTracker` and Tooling bundle export are INTEGRATED
Files likely involved: `FenBrowser.FenEngine` missing-API tracker and host dispatch, `FenBrowser.Tooling/Program.cs`, included tracker tests
Specs/references: Web IDL; DOM; `docs/MISSING_API_TRACKER.md`
Current behavior: Runtime records provenance, but the bundle exports a smaller capability list. Google observations include Closure expandos, wrong-receiver probes, legacy feature checks, and standards candidates as one undifferentiated list.
Expected behavior: The bundle preserves provenance and classifies `STANDARD_API`, `SITE_EXPANDO`, `WRONG_RECEIVER`, `LEGACY_PROBE`, or `UNCLASSIFIED`; only confirmed standard APIs feed priority counts.
Reproduction: Run a local page that reads one missing standard member, assigns/reads an expando, probes a wrong receiver, and performs legacy feature detection; then inspect `missing_apis.json`.
Root cause hypothesis: Tooling reads `EngineCapabilities` rather than the richer per-run tracker, and property-miss instrumentation lacks assignment/prototype/IDL-aware classification.
Implementation plan: Define v2 record, merge runtime records by stable key, add classifier inputs without mutating page behavior, map trace category to `WebIDL` or `DOM`, export classifications and causal links.
Tests required: All five dispositions, dedup/count/first-seen, source identity, cross-navigation isolation, redaction, and Google-name regression cases.
Evidence required: Before false-positive list and after classified local/Google bundles with no loss of provenance.
Security impact: Script URLs and messages require redaction/length limits.
Performance impact: Bound unique records per document and avoid allocating stacks on every repeated miss.
Compatibility impact: Improves attribution; does not add fake browser members.
Known risks: Misclassifying a true standard member or suppressing a causal probe.
Blockers: None
Next action: Add the mixed-disposition local fixture and lock the v2 JSON schema with a snapshot test.

## Task TRACE-003

Task ID: TRACE-003
Title: Implement deterministic first-causal-blocker classification
Area: Diagnostic spine / real-site attribution
Owner Agent: Diagnostic Agent
Status: TESTED
Priority: 1
Risk Level: Medium
Dependencies: Lifecycle, network, script, event-loop, style/layout, and raw trace artifacts are INTEGRATED
Files likely involved: New classifier under `FenBrowser.Tooling`, `FenBrowser.Tooling/Program.cs`, included Tooling/diagnostic tests
Specs/references: `docs/DIAGNOSTICS.md`; `docs/REAL_SITE_DEBUGGING.md`
Current behavior: `first_blocker.json` deterministically evaluates 19 navigation-through-interaction milestones, keeps post-load callback defects non-fatal, emits contradiction warnings, and marks unattempted interaction explicitly. Google reports `none`. The navigation transition's bounded event-loop sample is explicitly labeled `transition-time` and timed out, while current lifecycle/event-loop fields agree on complete/DCL/load.
Expected behavior: `first_blocker.json` names one earliest causal blocker, affected milestone, A-L bucket, subsystem owner, evidence records, and confidence; `none` is explicit when boot succeeds.
Reproduction: Use fixtures for navigation failure, script throw, missing API causing throw, late optional resource failure, zero-size root, and successful page.
Root cause: The prior summary had no normalized candidate model, milestone dependency graph, or fatality filter.
Implementation plan: Parse typed artifacts; normalize sequence/time; derive required milestones; filter non-fatal probes/late optional errors; rank by blocked milestone then causal sequence; emit typed result and summary rendering.
Tests required: One fixture per A-L-relevant implemented bucket, tie ordering, contradictory lifecycle sources, missing artifact, clock mismatch, successful page, and schema-version tests.
Evidence required: Deterministic repeated output and correct blocker for every fixture plus current Google result of `none` with remaining gaps listed separately.
Security impact: Do not embed secrets or unbounded payloads in evidence excerpts.
Performance impact: Offline/bundle-finalization work with bounded artifact sizes.
Compatibility impact: Changes diagnostics only; no page behavior.
Known risks: Causal inference presented as certainty; guard with evidence IDs and confidence.
Blockers: None
Next action: Add missing-artifact and clock-mismatch classifier fixtures; expand lifecycle coverage for async/defer/module/destruction separately from the now-labeled transition observation.

## Task TEST-001

Task ID: TEST-001
Title: Put diagnostic and browser-integration regressions on a discovered test surface
Area: Verification infrastructure
Owner Agent: Conformance Agent
Status: RESEARCHED
Priority: 1
Risk Level: Medium
Dependencies: `FenBrowser.Tests` builds and focused included tests pass
Files likely involved: `FenBrowser.Tests/FenBrowser.Tests.csproj`, diagnostic/event-loop test files or a new focused test project
Specs/references: `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md`; `docs/DEFINITION_OF_DONE.md`
Current behavior: `Engine/**`, `DOM/**`, `WebAPIs/**`, `Integration/**`, `Diagnostics/**`, `Rendering/**`, `Host/**`, and other directories are excluded. Release discovery finds missing-API and renderer metadata tests but not event-loop trace or real-site rendering diagnostics.
Expected behavior: Tests that protect TRACE-001 through TRACE-003 are compiled, discoverable, and run by one small documented command.
Reproduction: Compare `dotnet test --list-tests` output for named diagnostic test classes against source files.
Root cause hypothesis: Broad compile-removal patterns silently disconnect high-value regression files from the active test assembly.
Implementation plan: Inventory excluded tests and dependencies; choose the smallest coherent included project/surface; include only required files; resolve compile failures without broad production refactors; document the focused command.
Tests required: Test discovery assertion/list plus execution of the selected diagnostic slice.
Evidence required: Before/after discovered-test list and green focused run with exact counts.
Security impact: Enables deny-path and redaction regression tests.
Performance impact: Keep the default focused slice bounded; no full-suite requirement.
Compatibility impact: Verification-only.
Known risks: Surfacing stale tests that describe obsolete architecture.
Blockers: None
Next action: Produce an excluded-test inventory grouped by compile dependency and select the diagnostic subset only.

## Task TRACE-004

Task ID: TRACE-004
Title: Complete required bundle artifacts for IPC, sandbox, and performance
Area: Diagnostic spine / process / performance
Owner Agent: Process Diagnostic Agent
Status: NOT_STARTED
Priority: 1
Risk Level: Medium
Dependencies: Current bundle manifest, IPC logging, sandbox profiles, and render telemetry exist
Files likely involved: `FenBrowser.Tooling/Program.cs`, Host process-isolation diagnostics, render telemetry types, included tests
Specs/references: `docs/DIAGNOSTICS.md`; `docs/IPC_MODEL.md`; `docs/PERFORMANCE_DASHBOARD.md`
Current behavior: `ipc.json`, `sandbox_denials.json`, and `performance.json` are absent from the artifact manifest.
Expected behavior: Every run emits schema-versioned files, including explicit `inactive`/`no-denials` records when the process mode or data source is inactive.
Reproduction: Run one in-process local page, one explicit brokered local page, an IPC rejection fixture, and a frame-budget fixture.
Root cause hypothesis: Data producers and Tooling snapshots were developed separately and the bundle contract only lists implemented outputs.
Implementation plan: Define schemas; adapt bounded metadata snapshots; emit empty/inactive reason records; add manifest validation; preserve token/header redaction.
Tests required: Inactive mode, send/receive/reject/timeout, sandbox deny, frame/allocation metrics, manifest completeness, and redaction.
Evidence required: Four fixture bundles with complete manifests and cross-file correlation IDs.
Security impact: Never serialize tokens, payload bodies, cookies, or authorization headers.
Performance impact: Metadata must be bounded and collected without blocking hot paths.
Compatibility impact: Diagnostic-only additive artifacts.
Known risks: Sensitive-data leakage or high-volume IPC trace growth.
Blockers: None
Next action: Lock the three JSON schemas and an artifact-manifest completeness test.

## Task SITE-001

Task ID: SITE-001
Title: Automate Google search input and submission acceptance
Area: Real-site input / event / navigation
Owner Agent: Browser Integration Agent
Status: RESEARCHED
Priority: 1
Risk Level: Medium
Dependencies: Google document, scripts, DOM, layout, paint, and screenshot are TESTED in the current bundle
Files likely involved: `FenBrowser.Tooling`, WebDriver/automation hooks, Host input routing, BrowserApi activation/focus paths, local regression fixture
Specs/references: UI Events; HTML forms; WebDriver; `docs/REAL_SITE_TRACKER.md`
Current behavior: The search UI renders, but the current Google run does not prove focus, text editing, submit/default action, or resulting network/navigation. The deterministic local fixture passes six compiled/discovered behavior tests, and the generic `debug-site-interact` runner has two discovered tests plus a bundle proving hit-tested click, focus, typing, successful-control submit, terminal navigation, bounded event records, and before/after screenshots.
Expected behavior: Automation clicks the search control, enters a nonce, submits, observes value/input events and a terminal network/navigation outcome, and captures before/after screenshots.
Reproduction: Current Google URL at 1280x800 plus the same interaction on a local form fixture.
Root cause hypothesis: The local defects were split event registries, a click-only WebDriver path, and missing checkable-input checkedness. Google-specific residual behavior is unknown until the same sequence is correlated in one trace.
Implementation plan: Run the selector-driven command on Google; stop at the earliest failed acceptance milestone and reduce it without page-specific engine behavior.
Tests required: Hit test, focus, keyboard/text input, input/change/submit ordering, preventDefault, successful submit/navigation.
Evidence required: Before/after screenshots, input trace, DOM value, event/default-action records, request/navigation result, no crash/hang.
Security impact: Automation must not bypass page security or challenge behavior; do not persist user data.
Performance impact: Record input-to-visible-update and input-to-request latency.
Compatibility impact: Directly validates real-site usability.
Known risks: Site variation, consent UI, or network challenge; retain exact URL/run evidence.
Blockers: None
Next action: Run `debug-site-interact` against the current Google page with fresh selectors and a short unique nonce, then reduce the earliest failed milestone or record the terminal request/navigation result.

## Task WPT-001

Task ID: WPT-001
Title: Establish a selected browser-integration WPT baseline
Area: Conformance / regression
Owner Agent: Conformance Agent
Status: RESEARCHED
Priority: 2
Risk Level: Low
Dependencies: Local WPT checkout and category runner/results exist
Files likely involved: `scripts/` WPT runners, `Results/wpt_categories/`, `docs/TEST_BASELINE.md`
Specs/references: Local `C:\Users\udayk\Videos\wpt`; DOM, HTML, Fetch, Web IDL, CSSOM, UI Events
Current behavior: Retained aggregate reports 2,752 pass, 502 fail, 369 crash, and 822 timeout of 4,445, with 17 category errors; it is not a selected Gate 0 integration matrix.
Expected behavior: A small repeatable category set covers lifecycle, event loop, DOM/events, fetch/CORS, CSSOM/geometry, and forms with per-test terminal results.
Reproduction: Run the existing local category runner for the selected categories only.
Root cause hypothesis: Existing aggregate mixes capability gaps, runner crashes/timeouts, and category errors without a boot-pipeline priority view.
Implementation plan: Select categories by real-site dependency; purge result history older than 24 hours; run locally; separate runner errors from engine failures; record exact commands and artifact paths.
Tests required: The selected WPT categories themselves plus runner self-check.
Evidence required: Machine-readable results, pass/fail/crash/timeout counts, first shared failures, and updated baseline.
Security impact: Include at least one same-origin/CORS negative slice.
Performance impact: Record hangs/timeouts as diagnostic signals, not benchmarks.
Compatibility impact: Prioritizes browser integration over obscure conformance.
Known risks: Runner infrastructure may dominate failure counts.
Blockers: None
Next action: List available local category tags and choose the smallest six-category integration set without running a full suite.

## Task PROC-001

Task ID: PROC-001
Title: Audit one explicit brokered renderer real-site run
Area: Process isolation / IPC / crash containment
Owner Agent: Process Agent
Status: RESEARCHED
Priority: 2
Risk Level: High
Dependencies: Brokered coordinator and child-process implementations exist; explicit environment selection avoids changing the default
Files likely involved: `FenBrowser.Host/ProcessIsolation`, `FenBrowser.Tooling/Program.cs`, process diagnostic tests
Specs/references: `docs/PROCESS_MODEL.md`; `docs/IPC_MODEL.md`; `docs/SECURITY_MODEL.md`
Current behavior: Default real-site bundle is in-process; no current brokered Google evidence was found.
Expected behavior: Explicit brokered mode navigates, renders, accepts input, logs authenticated IPC, and contains a forced renderer failure without killing the UI process.
Reproduction: Use a local fixture first, then Google, with `FEN_PROCESS_ISOLATION=brokered`; record process IDs and mode in the bundle.
Root cause hypothesis: Unknown; likely integration gaps are child startup, log forwarding, shared-memory frame lifecycle, or automation routing.
Implementation plan: Capture clean process inventory; run local fixture; verify handshake/frame/input; induce a controlled test-only child exit; then run Google if local acceptance passes.
Tests required: Auth failure, timeout, oversized payload, stale generation, crash/restart/quarantine, UI survival.
Evidence required: Complete process/IPC/crash artifacts, screenshot, UI survival proof, and no orphan child.
Security impact: Do not relax sandbox or authentication to make the run pass.
Performance impact: Record startup, IPC counts/bytes, frame latency, and shared-memory use.
Compatibility impact: Identifies process-only regressions before changing defaults.
Known risks: Child launch/hang and native resource leakage.
Blockers: None for explicit audit; changing the default remains `BLOCK-PROC-001`.
Next action: Run the included coordinator policy tests and a local brokered fixture before any external site.

## Task BIND-001

Task ID: BIND-001
Title: Inventory active manual bindings against available WebIDL
Area: WebIDL / DOM architecture
Owner Agent: Bindings Agent
Status: RESEARCHED
Priority: 2
Risk Level: Low
Dependencies: Manual host runtime, WebIDL generator, and IDL inputs exist
Files likely involved: `FenBrowser.WebIdlGen`, `FenBrowser.FenEngine/Bindings`, `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs`, `docs/WEBIDL_BINDINGS_TRACKER.md`
Specs/references: Web IDL and the specifications linked by each selected interface
Current behavior: Generated sources are excluded and manual dispatch owns runtime exposure; exact overlap and drift are not machine-readable.
Expected behavior: A generated audit report maps each IDL member to manual implementation, missing implementation, excluded source, test coverage, and lifetime complexity without activating generated bindings.
Reproduction: Run the inventory over current IDL inputs and active host member registration.
Root cause hypothesis: Generator and runtime integration evolved independently, hiding duplicate, missing, and incompatible surfaces.
Implementation plan: Parse existing IDL metadata; extract active binding registrations; normalize interface/member names; emit report under `Results/`; update tracker with evidence only.
Tests required: Inventory parser/extractor fixtures and deterministic output.
Evidence required: Report with source paths and counts; one reviewed low-lifetime-risk candidate.
Security impact: Research only; no new exposure.
Performance impact: Offline tooling only.
Compatibility impact: Enables evidence-based migration.
Known risks: Reflection-based extraction could misrepresent dynamic registrations; prefer source/generator metadata.
Blockers: None for inventory; activation remains `BLOCK-MEM-001`.
Next action: Enumerate checked-in IDL interfaces and the active host registration tables into a deterministic report schema.

## Task PERF-001

Task ID: PERF-001
Title: Create a repeatable real-site performance capture
Area: Performance / diagnostics
Owner Agent: Performance Agent
Status: RESEARCHED
Priority: 2
Risk Level: Low
Dependencies: Stage telemetry, performance fixture, and Google bundle exist
Files likely involved: render telemetry, Tooling performance export, `docs/PERFORMANCE_DASHBOARD.md`
Specs/references: .NET runtime counters/profiling; browser frame-timing semantics
Current behavior: One Google frame records 56.8748 ms layout, 256.4934 ms paint, 101.6828 ms raster, and a 424.62 ms watchdog duration, without CPU/allocation/GC attribution or repeated distribution.
Expected behavior: Identical runs emit navigation/stage/frame distributions, managed/FenJS/native memory signals, GC pauses, long tasks, and correctness artifacts.
Reproduction: Fixed local performance fixture and Google at recorded viewport/build/process mode with identical settle and interaction sequence.
Root cause hypothesis: No bottleneck is assigned; paint is only the largest coarse stage in one sample.
Implementation plan: Define capture metadata; export bounded telemetry; collect repeated samples; attach CPU/allocation profile; rank only measured hotspots.
Tests required: Performance artifact schema, counter reset/isolation, low-overhead disabled mode, and unchanged rendering correctness.
Evidence required: Reproducible baseline distribution, profile artifact, and dashboard update; no optimization patch in this task.
Security impact: Profile and trace redaction follows diagnostic policy.
Performance impact: Measure profiler/telemetry overhead and keep normal mode bounded.
Compatibility impact: Measurement only.
Known risks: Diagnostic builds, cold caches, or network variation can distort results.
Blockers: None
Next action: Lock run metadata and `performance.json` schema on the local fixture before profiling Google.
