# FenBrowser Dependency-Ready Next Tasks

Snapshot date: 2026-08-22. Only tasks whose current dependencies are satisfied are listed. Order follows the diagnostic-first mission; it is not a calendar plan.

## Task TRACE-001

Task ID: TRACE-001
Title: Preserve callback failures and drain logs before bundle export
Area: Diagnostic spine / event loop / Tooling
Owner Agent: Diagnostic Agent
Status: REGRESSION_PROTECTED
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
Status: STUBBED
Priority: 1
Risk Level: Medium
Dependencies: Runtime `MissingApiTracker` and Tooling bundle export are INTEGRATED
Files likely involved: `FenBrowser.FenEngine` missing-API tracker and host dispatch, `FenBrowser.Tooling/Program.cs`, included tracker tests
Specs/references: Web IDL; DOM; `docs/MISSING_API_TRACKER.md`
Current behavior: Runtime sidecars and the bounded `debug-site` bundle use schema v2. The post-drain bundle retains up to 512 rich records across navigation/site transitions, preserves first and ordered read/write/descriptor/property-check kinds, descriptor target identity, assignment timing, and boolean function-prototype marker evidence, and reports truncation. Checked-in stringifier and partial-interface metadata now classify all 25 fresh Google observations: 11 standard, 11 page-owned, 2 wrong-receiver, and 1 legacy probe.
Expected behavior: The bundle preserves provenance and classifies `STANDARD_API`, `SITE_EXPANDO`, `WRONG_RECEIVER`, `LEGACY_PROBE`, or `UNCLASSIFIED`; only confirmed standard APIs feed priority counts.
Reproduction: Run a local page that reads one missing standard member, assigns/reads an expando, probes a wrong receiver, and performs legacy feature detection; then inspect `missing_apis.json`.
Root cause: Direct host reads/writes, descriptor and property-existence checks, boolean script-function prototype markers, IDL stringifiers, and partial interfaces are now attributable. `Location.toString` and `Navigator.geolocation` are genuine missing standard members, but current evidence does not make either causal to Google's accepted load/render/interaction milestones.
Implementation plan: Classification and export are implemented and regression-protected. Do not add runtime stubs. Select either missing member only when a deterministic local reduction or selected local WPT demonstrates required behavior and supplies descriptor, conversion, permission, and lifetime acceptance criteria.
Tests required: All five dispositions, dedup/count/first-seen, source identity, cross-navigation isolation, redaction, and Google-name regression cases.
Evidence required: Before false-positive list and after classified local/Google bundles with no loss of provenance.
Security impact: Script URLs and messages require redaction/length limits.
Performance impact: Bound unique records per document and avoid allocating stacks on every repeated miss.
Compatibility impact: Improves attribution; does not add fake browser members.
Known risks: Misclassifying a true standard member or suppressing a causal probe.
Blockers: None
Next action: Leave the two non-causal standard misses visible; TRACE-003 now covers missing artifacts and clock inversion, so continue with its schema-version/malformed-artifact boundary or another higher-value dependency.

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
Current behavior: `first_blocker.json` deterministically evaluates 19 navigation-through-interaction milestones, keeps post-load callback defects non-fatal, emits contradiction and bounded evidence-quality warnings, and marks unattempted interaction explicitly. Missing required artifacts and causal-sequence timestamp inversions produce `insufficient-evidence` owned by Tooling/Diagnostics. Schema-version stability is pinned across all three result shapes; malformed artifacts (unparseable causal timestamps, whitespace/duplicate/null artifact names, null collections) degrade deterministically without fabricating confidence, and unvalidated timestamps never enter the typed artifact. Fresh Google reports `none`, 0 evidence-quality warnings, and 26/26 artifacts.
Expected behavior: `first_blocker.json` names one earliest causal blocker, affected milestone, A-L bucket, subsystem owner, evidence records, and confidence; `none` is explicit when boot succeeds.
Reproduction: Use fixtures for navigation failure, script throw, missing API causing throw, late optional resource failure, zero-size root, and successful page.
Root cause: The prior summary had no normalized candidate model, milestone dependency graph, or fatality filter.
Implementation plan: Parse typed artifacts; normalize sequence/time; derive required milestones; filter non-fatal probes/late optional errors; rank by blocked milestone then causal sequence; emit typed result and summary rendering.
Tests required: One fixture per A-L-relevant implemented bucket, tie ordering, contradictory lifecycle sources, missing artifact, clock mismatch, successful page, malformed artifact, and schema-version tests.
Evidence required: Deterministic repeated output and correct blocker for every fixture plus current Google result of `none` with remaining gaps listed separately.
Security impact: Do not embed secrets or unbounded payloads in evidence excerpts.
Performance impact: Offline/bundle-finalization work with bounded artifact sizes.
Compatibility impact: Changes diagnostics only; no page behavior.
Known risks: Causal inference presented as certainty; guard with evidence IDs and confidence.
Blockers: None
Next action: Expand lifecycle coverage for async/defer/module/destruction separately from the now-labeled transition observation.

## Task TEST-001

Task ID: TEST-001
Title: Put diagnostic and browser-integration regressions on a discovered test surface
Area: Verification infrastructure
Owner Agent: Conformance Agent
Status: TESTED
Priority: 1
Risk Level: Medium
Dependencies: `FenBrowser.Tests` builds and focused included tests pass
Files likely involved: `FenBrowser.Tests/FenBrowser.Tests.csproj`, diagnostic/event-loop test files or a new focused test project
Specs/references: `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md`; `docs/DEFINITION_OF_DONE.md`
Current behavior: The TRACE-001 through TRACE-003 regression suites (event-loop trace, callback-failure provenance, missing-API classification, first-blocker classifier, bundle artifact contracts) are compiled and discovered, protected by `RequiredBrowserIntegrationDiscoveryTests`. `scripts/test_inventory.ps1` now generates the deterministic excluded-source inventory (`Results/test-inventory/`, schema fenbrowser.test-inventory/1): 243 of 538 sources excluded across 16 groups. The PII-safe cookie diagnostics regression (`Diagnostics/CookieDiagnosticsTests.cs`, 5 tests) is surfaced and joins the non-parallel EngineLog collection with explicit per-test `EngineLog.Configure`; it passes focused and in the full-suite run.
Expected behavior: Tests that protect TRACE-001 through TRACE-003 are compiled, discoverable, and run by one small documented command; the excluded surface is inventoried by script rather than folklore.
Reproduction: `powershell -ExecutionPolicy Bypass -File scripts/test_inventory.ps1`; `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --filter "FullyQualifiedName~CookieDiagnostics"`.
Root cause hypothesis: Resolved for the diagnostic subset. Broad compile-removal patterns remain for legacy trees (Engine 110, Rendering 47, DOM 16 files) pending independent review.
Implementation plan: Inventory produced; diagnostic subset active. Remaining exclusions require per-group compile-dependency review before inclusion.
Tests required: Test discovery assertion/list plus execution of the selected diagnostic slice.
Evidence required: Before/after discovered-test list and green focused run with exact counts.
Security impact: Enables deny-path and redaction regression tests (cookie PII redaction now active).
Performance impact: Keep the default focused slice bounded; no full-suite requirement.
Compatibility impact: Verification-only.
Known risks: Full-suite runs still show ~108 nondeterministic failures from cross-collection global-state races (EngineLog/process-wide singletons); this is recorded as its own follow-up and does not affect focused slices.
Blockers: None
Next action: Group-review the remaining 243 excluded sources by compile dependency (start with `Diagnostics/NavigationGlobalsProbeTests.cs`, which references the removed production probe type and needs a decision: restore the probe or retire the test).

## Task TRACE-004

Task ID: TRACE-004
Title: Complete required bundle artifacts for IPC, sandbox, and performance
Area: Diagnostic spine / process / performance
Owner Agent: Process Diagnostic Agent
Status: IMPLEMENTED
Priority: 1
Risk Level: Medium
Dependencies: Current bundle manifest, IPC logging, sandbox profiles, and render telemetry exist
Files likely involved: `FenBrowser.Tooling/Program.cs`, Host process-isolation diagnostics, render telemetry types, included tests
Specs/references: `docs/DIAGNOSTICS.md`; `docs/IPC_MODEL.md`; `docs/PERFORMANCE_DASHBOARD.md`
Current behavior: Every in-process local run emits and manifests schema-v1 `ipc.json`, `sandbox_denials.json`, and `performance.json`. IPC/sandbox records truthfully state inactive/not-configured with zero events/denials; performance is labeled as one partial diagnostic sample and names unavailable metrics.
Expected behavior: Every run emits schema-versioned files, including explicit `inactive`/`no-denials` records when the process mode or data source is inactive.
Reproduction: Run one in-process local page, one explicit brokered local page, an IPC rejection fixture, and a frame-budget fixture.
Root cause: The Tooling writer and manifest omitted all three artifacts even though partial render telemetry and process-mode knowledge were available.
Implementation plan: Completed for the in-process empty/partial contract. Next adapt bounded brokered IPC and sandbox-denial snapshots, then add repeatable performance metadata without serializing tokens or payload bodies.
Tests required: Inactive mode, send/receive/reject/timeout, sandbox deny, frame/allocation metrics, manifest completeness, and redaction.
Evidence required: Four fixture bundles with complete manifests and cross-file correlation IDs.
Security impact: Never serialize tokens, payload bodies, cookies, or authorization headers.
Performance impact: Metadata must be bounded and collected without blocking hot paths.
Compatibility impact: Diagnostic-only additive artifacts.
Known risks: Sensitive-data leakage or high-volume IPC trace growth.
Blockers: `BLOCK-PROC-002` prevents brokered local evidence; it does not block the in-process artifact contract.
Next action: After `BLOCK-PROC-002`, populate `ipc.json` and `sandbox_denials.json` from one local brokered rejection/denial fixture without changing public IPC contracts.

## Task SITE-001

Task ID: SITE-001
Title: Automate Google search input and submission acceptance
Area: Real-site input / event / navigation
Owner Agent: Browser Integration Agent
Status: TESTED
Priority: 1
Risk Level: Medium
Dependencies: Google document, scripts, DOM, layout, paint, and screenshot are TESTED in the current bundle
Files likely involved: `FenBrowser.Tooling`, WebDriver/automation hooks, Host input routing, BrowserApi activation/focus paths, local regression fixture
Specs/references: UI Events; HTML forms; WebDriver; `docs/REAL_SITE_TRACKER.md`
Current behavior: The current Google interaction bundle proves hit-tested textarea focus, seven accepted nonce characters, keyboard/input/change/blur ordering, visible submit-control click, form submit, a successful 200 GET `/search` navigation, and Google's delayed redirect to a genuine HTTP 429 `/sorry/` challenge. Eight compiled form tests, five Tooling tests, and one render-generation test protect current-layout/pointer-events hit testing, full CSS lookup, fixed diagnostic viewport, ordinary and chained local form navigation, bounded event export, screenshot artifact ownership, and stale CSS/render publication. Terminal lifecycle, active DOM/rendered text, and the visibly different after screenshot all describe navigation 3's challenge document; callbacks and exceptions remain zero.
Expected behavior: Automation clicks the search control, enters a nonce, submits, observes value/input events and a terminal network/navigation outcome, and captures before/after screenshots.
Reproduction: Current Google URL at 1280x800 plus the same interaction on a local form fixture.
Root cause hypothesis: Resolved. The blockers were stale layout/style/paint snapshots, a Tooling viewport mismatch, a concrete-host lookup overload that bypassed full WebDriver selector semantics, early terminal-generation selection, and older detached CSS/script work republishing render state after replacement navigation.
Implementation plan: Keep the local form, chained-navigation, and overlapping-render reductions on the active test surface. Do not bypass Google's challenge; rerun only when a relevant engine change needs real-site confirmation.
Tests required: Hit test, focus, keyboard/text input, input/change/submit ordering, preventDefault, successful submit/navigation.
Evidence required: Before/after screenshots, input trace, DOM value, event/default-action records, request/navigation result, no crash/hang.
Security impact: Automation must not bypass page security or challenge behavior; do not persist user data.
Performance impact: Record input-to-visible-update and input-to-request latency.
Compatibility impact: Directly validates real-site usability.
Known risks: Site variation, consent UI, or network challenge; retain exact URL/run evidence.
Blockers: Google's current HTTP 429 challenge prevents normal search-result rendering; this is external security behavior and is not an engine blocker or bypass target.
Next action: The lifecycle WPT slice is closed (see WPT-001); continue with its fetch/CORS or cookies slice and preserve the same two-run exact-classification evidence.

## Task WPT-001

Task ID: WPT-001
Title: Establish a selected browser-integration WPT baseline
Area: Conformance / regression
Owner Agent: Conformance Agent
Status: TESTED
Priority: 2
Risk Level: Low
Dependencies: Local WPT checkout and category runner/results exist
Files likely involved: `FenBrowser.Tooling/WptToolRunner.cs`, active DOM token-list bindings, `Results/wpt/selected/`, `docs/TEST_BASELINE.md`
Specs/references: Local `C:\Users\udayk\Videos\wpt`; DOM, HTML, Fetch, Web IDL, CSSOM, UI Events
Current behavior: The four-file lifecycle gate (DOM token-list stringifier/value, checkbox click activation, `document-readyState.html`) passes cleanly in repeated runs at local WPT `88152b84`. The fetch/CORS slice was added and exactly classified at FenBrowser `eb74bec0`: `Results/wpt/selected/20260822_fetch_cors_gate_run1/` and `-run2/` produce byte-identical failure sets of 16 records across three root-cause buckets (`no-cors` opaque filtering absent; cross-origin requests without CORS headers not rejected; `Response.type` never set) plus a wholesale dedicated-worker scope timeout classified as a capability gap. Same-origin fetch subtests pass. See `docs/TEST_BASELINE.md` for the classification table.
Expected behavior: A small repeatable category set covers lifecycle, event loop, DOM/events, fetch/CORS, CSSOM/geometry, and forms with per-test terminal results.
Reproduction: Run the existing local category runner for the selected categories only.
Root cause hypothesis: The initial DOM/forms gate is closed. The retained broad aggregate still mixes capability and infrastructure classes, so expansion must remain file-scoped and classification-first.
Implementation plan: Preserve the exact passing gate, then add cookies, CSSOM/geometry, and layout files one dependency-ready slice at a time without mixing infrastructure failures with engine assertions. CORS slice joins the passing gate only after FETCH-001 clears its three buckets.
Tests required: The selected WPT categories themselves plus runner self-check.
Evidence required: `Results/wpt/selected/20260821_lifecycle_gate_run2/`, `Results/wpt/selected/20260821_lifecycle_gate_run3/`, `Results/wpt/selected/20260822_fetch_cors_gate_run1/`, `Results/wpt/selected/20260822_fetch_cors_gate_run2/`, focused local reductions, and updated exact classifications.
Security impact: The CORS negative slice now exists and documents that enforcement is currently bypassed in the JS-visible path; FETCH-001 owns the Priority 0 adjacent fix.
Performance impact: Record hangs/timeouts as diagnostic signals, not benchmarks.
Compatibility impact: Prioritizes browser integration over obscure conformance.
Known risks: Runner infrastructure may dominate failure counts.
Blockers: None
Next action: The fetch/CORS and cookies slices are classified (see FETCH-001 and FRAME-001). Select one deterministic CSSOM/geometry file from the local WPT checkout, run it with the existing gate twice, and classify any failure before widening further.

## Task FETCH-001

Task ID: FETCH-001
Title: Wire CORS enforcement and response filtering into the JS-visible fetch path
Area: Fetch / CORS / security
Owner Agent: Network Agent
Status: RESEARCHED
Priority: 1
Risk Level: Medium
Dependencies: Two-run exact-classification evidence exists (`Results/wpt/selected/20260822_fetch_cors_gate_run1/-run2`); same-origin fetch path works
Files likely involved: `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs` (JS-visible fetch handler), `FenBrowser.FenEngine/Rendering/BrowserApi.cs` (`FetchHandler` wiring), `FenBrowser.Core` resource/network policy surfaces
Specs/references: Fetch Standard (response filtering, basic/cors/opaque response types, CORS check); HTML Standard (sec-fetch mode); `docs/NETWORK_FETCH_TRACKER.md`
Current behavior: Cross-origin `fetch(url)` resolves even when the server forbids CORS (5 subtests expect rejection with TypeError). `no-cors` mode exposes real status 200 instead of an opaque-filtered response with status 0. Successful CORS responses report `Response.type` undefined instead of `"cors"`. Policy surfaces exist in the resource layer but do not reach the JS-visible Response construction path.
Expected behavior: `fetch` applies mode/credentials checks against the resource-layer CORS decision; responses are filtered per mode so scripts observe `type` of `basic`/`cors`/`opaque`, opaque responses expose status 0 with blocked body/headers, and forbidden cross-origin reads reject with TypeError.
Reproduction: Run the five-file gate twice per WPT-001; inspect the three buckets in `wpt.failures.json`.
Root cause hypothesis: The JS-side fetch implementation constructs Response values directly from successful HTTP results without consulting the resource layer's CORS verdict or applying any response filter.
Implementation plan: Thread mode/credentials into `FetchHandler`; map the resource-layer CORS decision to reject-with-TypeError; introduce a response filter step that stamps `type` and applies opaque/basic filtering before Response construction; keep same-origin behavior unchanged.
Tests required: The cors-basic slice rerun twice to green; local reduction fixture for no-cors opacity and forbidden-CORS rejection; regression test that same-origin `Response.type` is `basic`.
Evidence required: Before: the two 2026-08-22 run bundles; after: two green runs of the six-file gate plus a local fixture bundle showing correct types/statuses.
Security impact: Priority 0 adjacent — this closes the gap where script can read cross-origin response data the server did not expose via CORS headers.
Performance impact: Filtering is O(1) metadata work on the existing response object; no extra network round trips.
Compatibility impact: Aligns JS-visible fetch semantics with browsers; sites relying on unrestricted cross-origin reads will surface errors, which matches standard behavior.
Known risks: Overly strict enforcement could break current real-site loads if any same-site asset is served from a sibling origin without CORS headers; verify against the Google bundle before/after.
Blockers: None for the fix itself; the file is currently contended by another active session, so coordinate before editing `BrowserScriptEngineRuntime.cs`.
Next action: Implement the response filter and CORS wiring, add the local reduction fixture, then move `fetch/api/cors/cors-basic.any.js` into the passing gate after two green runs.

## Task FRAME-001

Task ID: FRAME-001
Title: Dispatch load event on iframe elements when child documents finish loading
Area: DOM / frames / real-site boot
Owner Agent: Browser Integration Agent
Status: TESTED
Priority: 1
Risk Level: Medium
Dependencies: Child-frame navigation and parsing exist (trace-proven); local reduction bundle exists
Files likely involved: `FenBrowser.FenEngine/Rendering/BrowserApi.cs` (`LoadFrameElementAsync`), `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs` (event dispatch)
Specs/references: HTML Standard (iframe load event steps); `docs/DOM_API_TRACKER.md`
Current behavior: FIXED. `LoadFrameElementAsync` now dispatches a non-bubbling, non-cancelable `load` event on the parent iframe element after the child document attaches and its scripts initialize. Reduction evidence before: `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_logs_fixtures_iframe_cookie_chain_probe.html/20260821T191845Z/` (status frozen at `parent-script-ran`). After: `.../20260821T194355Z/` renders `parent-script-ran|doc-capture-load|iframe-load|cw=object|getCookies-missing|timer-alive` — both document-capture and element listeners fire, `contentWindow` resolves, no exceptions.
Expected behavior: After a child frame's document completes loading, the parent's iframe element dispatches `load`; re-navigation re-dispatches after the stale-document guard.
Reproduction: Run `debug-site file:///...logs/fixtures/iframe_cookie_chain_probe.html 12000` and read the status div chain.
Root cause: The frame-loading path completed child document setup without mapping child completion to a parent-element event dispatch.
Implementation plan: Done in `BrowserApi.DispatchFrameLoadCompleted`. Remaining follow-ups tracked separately: regression protection needs either a BrowserHost-level harness test or inclusion of the already-written excluded assertions in `FenBrowser.Tests/Engine/JavaScriptEngineLifecycleTests.cs` (iframe onload cases exist there but the whole `Engine/**` tree is compile-excluded — see TEST-001).
Tests required: Included regression test for load dispatch on child completion (pending harness/exclusion decision); rerun cookies gate slice after WINDOW-001.
Evidence required: Before/after reduction bundles above; clean rebuild note — Tooling runs debug-site in-process, so Tooling must be rebuilt to pick up FenEngine changes (stale-binary trap cost several diagnostic cycles here).
Security impact: None directly; enables future sandboxed-iframe attribute enforcement work on the same code path.
Performance impact: One event dispatch per frame load; negligible.
Compatibility impact: Unblocks iframe-gated boot patterns; the WPT cookies family now progresses past its first helper only after WINDOW-001 exposes cross-frame functions.
Known risks: Double dispatch across re-navigation guarded by the existing `IsFrameScriptsHydratedForUri` early-return; failure-path frames (fetch errors) intentionally do not dispatch yet — spec says error pages load, so revisit when error-page documents exist.
Blockers: None
Next action: WINDOW-001 (cross-frame `contentWindow` function exposure) before rerunning the cookies gate slice.

## Task WINDOW-001

Task ID: WINDOW-001
Title: Expose child-frame script functions through contentWindow
Area: DOM / frames / cross-realm bindings
Owner Agent: Bindings Agent
Status: RESEARCHED
Priority: 2
Risk Level: Medium
Dependencies: FRAME-001 landed; reduction shows `cw=object|getCookies-missing`
Files likely involved: `FenBrowser.FenEngine/Rendering/BrowserApi.cs`, `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs` (frame facade/window proxy surfaces; `IFrameInterpreterIsolationTests` shows `_iframeRealms`)
Specs/references: HTML Standard (WindowProxy, same-origin cross-window property access); `docs/MEMORY_MODEL.md`
Current behavior: `iframe.contentWindow` returns an object, but functions defined by the child frame's scripts (e.g. WPT cookie helpers' `getCookies`) are not reachable through it, so helper chains like echo-cookie.html + `win.getCookies()` cannot run.
Expected behavior: Same-origin access through `contentWindow` reaches the child realm's globals (functions, variables) with correct wrapper identity; cross-origin access stays blocked per origin policy.
Reproduction: The reduction fixture marks `getCookies-missing`.
Root cause hypothesis: The window facade exposed to parents does not forward property gets into the child frame realm's global object.
Implementation plan: Route same-origin `contentWindow` property gets/sets to the child realm's global with identity caching; keep postMessage path intact; enforce origin checks before forwarding.
Tests required: Extend the reduction chain to reach `expire-done`; include IFrameInterpreterIsolation/RealmSecurity suites; then rerun the cookies WPT slice twice.
Security impact: Cross-origin leakage risk if origin checks are skipped; negative tests required before DONE.
Performance impact: One indirection per property access; negligible.
Compatibility impact: Required by WPT cookies helpers and common embed APIs (Stripe/analytics windows).
Known risks: Realm lifetime management — child realm must live while parent holds contentWindow (memory-model review per BLOCK-MEM-001 adjacency).
Blockers: `BrowserScriptEngineRuntime.cs` contention with another active session may still apply.
Next action: Implement same-origin forwarding behind origin checks with the reduction chain as acceptance.

## Task PROC-001

Task ID: PROC-001
Title: Prove one explicit brokered renderer local run
Area: Process isolation / IPC / crash containment
Owner Agent: Process Agent
Status: BLOCKED_NEEDS_HUMAN_DECISION
Priority: 2
Risk Level: High
Dependencies: Brokered coordinator and child-process implementations exist; explicit environment selection avoids changing the default
Files likely involved: `FenBrowser.Host/ProcessIsolation`, `FenBrowser.Tooling/Program.cs`, process diagnostic tests
Specs/references: `docs/PROCESS_MODEL.md`; `docs/IPC_MODEL.md`; `docs/SECURITY_MODEL.md`
Current behavior: The compiled local real-process acceptance is discovered but skipped. Correct apphost resolution and Unicode child-environment propagation are tested, while strict AppContainer spawn fails with native error 2 because the development runtime path is not readable/executable by the renderer profile. WebDriver `GetCurrentUrl` now resolves the metadata-synced renderer URL when an out-of-process renderer is active (`b20fa485`), regression-protected by `HostBrowserDriverCurrentUrlTests` (`78198bcb`); the local engine URI alone reports the stale pre-navigation document in that mode.
Expected behavior: Explicit brokered mode navigates, renders, accepts input, logs authenticated IPC, and contains a forced renderer failure without killing the UI process.
Reproduction: Use a local fixture first, then Google, with `FEN_PROCESS_ISOLATION=brokered`; record process IDs and mode in the bundle.
Root cause: Runtime-file ACL provisioning for the renderer AppContainer is not defined. A temporary exact-SID ACL experiment removed the immediate spawn error but exposed a later unresolved timeout; automatic ACL mutation is not authorized.
Implementation plan: After `BLOCK-PROC-002` is decided, provision only the reviewed runtime path; re-enable the compiled local fixture; verify handshake/frame/input; induce a controlled test-only child exit; then run Google only if local acceptance passes.
Tests required: Auth failure, timeout, oversized payload, stale generation, crash/restart/quarantine, UI survival.
Evidence required: Complete process/IPC/crash artifacts, screenshot, UI survival proof, and no orphan child.
Security impact: Do not relax sandbox or authentication to make the run pass.
Performance impact: Record startup, IPC counts/bytes, frame latency, and shared-memory use.
Compatibility impact: Identifies process-only regressions before changing defaults.
Known risks: Child launch/hang and native resource leakage.
Blockers: `BLOCK-PROC-002` for AppContainer runtime-path access; changing the default remains `BLOCK-PROC-001`.
Next action: Obtain the runtime ACL/deployment decision for `BLOCK-PROC-002`; then re-enable the existing local acceptance before any external site.

## Task BIND-001

Task ID: BIND-001
Title: Inventory active manual bindings against available WebIDL
Area: WebIDL / DOM architecture
Owner Agent: Bindings Agent
Status: TESTED
Priority: 2
Risk Level: Low
Dependencies: Manual host runtime, WebIDL generator, and IDL inputs exist
Files likely involved: `FenBrowser.WebIdlGen`, `FenBrowser.FenEngine/Bindings`, `FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs`, `docs/WEBIDL_BINDINGS_TRACKER.md`
Specs/references: Web IDL and the specifications linked by each selected interface
Current behavior: `FenBrowser.Tooling webidl-inventory` deterministically maps all 60 checked-in definition records and 421 members to bounded manual source evidence, generated output policy, active tests, selected WPT correlations, lifetime complexity, and migration risk. It reports 268 members with manual evidence, 319 with active-test correlations, 74 with selected-WPT correlations, zero generated outputs present or compiled, and names `EventInit` as a conversion-only future candidate.
Expected behavior: A generated audit report maps each IDL member to manual implementation, missing implementation, excluded source, test coverage, and lifetime complexity without activating generated bindings.
Reproduction: Run `dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- webidl-inventory --output-dir Results/webidl/manual-binding-inventory --wpt-root C:/Users/udayk/Videos/wpt --selected-wpt dom/lists/DOMTokenList-stringifier.html,dom/lists/DOMTokenList-value.html,html/semantics/forms/the-input-element/checkbox-click-events.html`.
Root cause hypothesis: Generator and runtime integration evolved independently, hiding duplicate, missing, and incompatible surfaces.
Implementation plan: Keep the inventory read-only and generated bindings excluded. Review the `EventInit` evidence against the active dictionary-conversion call path only after the memory/lifetime decision boundary is addressed.
Tests required: Inventory parser/extractor fixtures and deterministic output.
Evidence required: `Results/webidl/manual-binding-inventory/`; one compiled deterministic fixture; matching JSON/Markdown hashes across identical runs; reviewed `EventInit` candidate limitations.
Security impact: Research only; no new exposure.
Performance impact: Offline tooling only.
Compatibility impact: Enables evidence-based migration.
Known risks: Reflection-based extraction could misrepresent dynamic registrations; prefer source/generator metadata.
Blockers: None for inventory; activation remains `BLOCK-MEM-001`.
Next action: `BLOCKED_NEEDS_HUMAN_DECISION` for generated-binding activation: select the authoritative wrapper/cross-heap ownership model using `docs/MEMORY_MODEL.md`. Independently continue with the brokered-renderer local proof; do not activate `EventInit` or other generated bindings.

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

## Task BIND-002

Task ID: BIND-002
Title: Implement PerformanceObserver constructor surface
Area: WebIDL / performance timeline bindings
Owner Agent: Bindings Agent
Status: NOT_STARTED
Priority: 2
Risk Level: Low
Dependencies: None; missing-API tracking and host-object plumbing are INTEGRATED
Files likely involved: FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs (global installation), FenBrowser.FenEngine/WebAPIs performance surface, WebIdlMemberCatalog/HostApiSurfaceCatalog, included binding tests
Specs/references: Performance Timeline Level 2; User Timing; local WPT checkout C:\Users\udayk\Videos\wpt (performance-timeline/)
Current behavior: new PerformanceObserver(cb) throws ReferenceError: PerformanceObserver is not defined. MediaWiki's experiment bootstrap (suggestionMode.js -> getRawHeader) constructs it inside a Promise executor, producing 4 unhandled promise rejections per Wikipedia load (after-bundle logs/real-site/en.wikipedia.org/20260821T191613Z/exceptions.json); the missing-API tracker already classifies it as a standard API.
Expected behavior: Constructor requires a callable callback (TypeError otherwise). Instances expose observe(options), disconnect(), takeRecords(); observe validates entryTypes/type per spec (throw TypeError on empty/invalid rather than silently no-op) and may deliver an empty buffer until mark/paint entry sources exist. Registration updates the missing-API catalog so Wikipedia observations stop being reported as missing.
Reproduction: Load the Wikipedia after-bundle reproduction command; or evaluate typeof PerformanceObserver and new PerformanceObserver(()=>{}) in the Tooling harness.
Root cause: The global was never installed; no observer registry exists behind the existing performance object.
Implementation plan: Install a native constructor backed by a small observer host (bounded record buffer, callback delivery queued through the engine event loop like MutationObserver delivery); wire supported entry types to whatever the existing performance.mark/paint telemetry can emit today, leaving undeliverable entry types registered-but-empty rather than stubbed-with-fake-data; add tracker/catalog metadata.
Tests required: Constructor arity/TypeError cases, observe validation (empty entryTypes, unknown type), disconnect idempotence, takeRecords drain, callback delivery via performance.mark + matching entry type, missing-API regression flipping to implemented.
Evidence required: Local fixture bundle with zero new missing-API observations for PerformanceObserver; focused tests green.
Security impact: None beyond existing callback sandboxing; callbacks must run under the standard engine lock and session-generation guards.
Performance impact: Observer records must stay bounded (reuse MutationObserver-style caps).
Compatibility impact: Removes 4 real-site promise rejections per Wikipedia load.
Known risks: Overpromising entry types that cannot be produced yet; mitigate by registering only producible types.
Blockers: None
Next action: Land constructor+observe/disconnect/takeRecords behind event-loop delivery, then rerun the Wikipedia passive bundle expecting zero PerformanceObserver rejections.

## Task JS-001

Task ID: JS-001
Title: Make Function.prototype.apply accept array-like host objects
Area: FenJS intrinsics / host interop
Owner Agent: JS Engine Agent
Status: NOT_STARTED
Priority: 2
Risk Level: Medium
Dependencies: Host property-resolution path for array-like reads is INTEGRATED
Files likely involved: FenBrowser.Js/Interpreter/BytecodeInterpreter.Calls.cs (apply intrinsic), host object property read path, FenBrowser.Js.Tests / FenBrowser.Tests/Engine coverage
Specs/references: ECMAScript 262 Function.prototype.apply (argArray Get("length") + indexed reads)
Current behavior: f.apply(thisArg, argArray) throws TypeError: Function.prototype.apply: second argument must be an object or null/undefined when argArray is a host-object wrapper that exposes length/indexed properties (jQuery/Sizzle find passes such results). Wikipedia after-bundle shows 4 timer-callback TypeErrors through find [op=CallMethodN] (.../20260821T191613Z/exceptions.json).
Expected behavior: Per spec, null/undefined short-circuit to zero args; any other value goes through ordinary Get for length and indices — array-like host wrappers therefore spread correctly instead of throwing. Plain non-objects that are not null/undefined still throw TypeError.
Reproduction: Local reduction (function(){return arguments.length}).apply(null, document.querySelectorAll('body')) style host array-like; plus the Wikipedia bundle stacks above.
Root cause hypothesis: The apply intrinsic switches on JsValue tag/kind and rejects HostObject values before attempting the generic length/indexed property reads.
Implementation plan: Route argArray preparation through the same generic property-read helper used by spread of host collections; keep fast paths for native JS arrays; preserve TypeError for primitives.
Tests required: Host-array-like spread (length + indexed via host getter), null/undefined argArray, primitive number/string argArray TypeError, length-out-of-range clamping, jQuery-shaped find regression mirroring the site stack.
Evidence required: Focused tests green + Wikipedia passive bundle showing zero apply TypeErrors from find.
Security impact: None (argument-count bounds already enforced elsewhere; keep existing max-args guard).
Performance impact: Keep the existing fast path first; host fallback only on non-fast tags.
Compatibility impact: Unblocks jQuery DOM-traversal paths across many sites.
Known risks: Host getters with side effects during argument materialization; acceptable — identical to property-access semantics elsewhere.
Blockers: None
Next action: Write the local host-array-like reduction test, patch the intrinsic, then rerun the Wikipedia passive bundle.