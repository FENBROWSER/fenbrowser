# FenBrowser Test Baseline

Snapshot date: 2026-07-14; focused build/test revalidated 2026-07-15. All paths and results are local. No conformance data in this file was fetched from the internet.

## Verification run in this audit

| Surface | Command | Result | Status |
| --- | --- | --- | --- |
| Tooling dependency graph, Release | `dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release -v minimal` | 0 errors, 498 warnings | TESTED |
| Diagnostic/process focused tests | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MissingApiTrackerTests|FullyQualifiedName~NavigationLifecycleTraceTests|FullyQualifiedName~RendererIpcMetadataTests|FullyQualifiedName~RendererIsolationPoliciesTests"` | 44 passed, 0 failed, 0 skipped | TESTED |
| Callback provenance/export focused tests | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CallbackFailureDiagnosticsTests|FullyQualifiedName~EngineLogSettingsTests.Flush_DrainsAcceptedEventsBeforeArtifactCopy|FullyQualifiedName~DebugSiteExceptionSummaryTests" --logger "console;verbosity=minimal"` | 9 passed, 0 failed, 0 skipped | TESTED |
| First-causal-blocker classifier fixtures | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FirstBlockerClassifierTests" --logger "console;verbosity=minimal"` | 12 passed, 0 failed, 0 skipped | TESTED |
| Lifecycle transition-detail and classifier slice | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~BrowserLifecycleDetailTests|FullyQualifiedName~NavigationLifecycleTrackerTests|FullyQualifiedName~FirstBlockerClassifierTests" --logger "console;verbosity=minimal"` | 18 passed, 0 failed, 0 skipped | TESTED |
| DOM host-collection iteration | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FenJsDomCollectionIterationTests" --logger "console;verbosity=minimal"` | 3 passed, 0 failed, 0 skipped; all 3 listed by `--list-tests` | REGRESSION_PROTECTED |
| Local form interaction acceptance | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --filter FullyQualifiedName~BrowserFormInteractionAcceptanceTests --no-restore --logger "console;verbosity=minimal"` | 6 passed, 0 failed, 0 skipped; all 6 listed by `--list-tests` | REGRESSION_PROTECTED |
| Adjacent form/input event slice | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FormControlActivationTests|FullyQualifiedName=FenBrowser.Tests.Scripting.FenJsInputEventDispatchTests.DispatchEventForElement_DeliversDoubleClickContextMenuAndPointerPayload|FullyQualifiedName=FenBrowser.Tests.Scripting.FenJsInputEventDispatchTests.DispatchEventForElement_EventListenerCanAccessFreshClassList" --logger "console;verbosity=minimal"` | 5 passed, 0 failed, 0 skipped | TESTED |
| FenJS iterator and stale-handle slice | `dotnet test FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~AtIteratorDispatchTests|FullyQualifiedName~ForOfTests|FullyQualifiedName~IteratorStaleHandleTests" --logger "console;verbosity=minimal"` | 24 passed, 0 failed, 0 skipped | TESTED |
| FenJS host-handle table slice | `dotnet test FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HostObjectTableTests" --logger "console;verbosity=minimal"` | 7 passed, 0 failed, 0 skipped | TESTED |
| Tooling dependency graph, Debug | Same build in Debug | Reached project compilation, then failed copying Host dependencies because Visual Studio and a running FenBrowser.Host locked Debug DLLs | RESEARCHED |

The current Release warnings are primarily existing obsolete-API, platform-guard, analyzer, and Tooling unreachable-code warnings. They do not fail the build, but they remain visible baseline debt. The Debug result is an environment lock, not a source compilation failure. The active processes were not terminated because they belong to the user's live workspace session.

## Test discovery gap

`FenBrowser.Tests/FenBrowser.Tests.csproj` removes `Engine/**`, `DOM/**`, `WebAPIs/**`, `Workers/**`, `Interaction/**`, `Integration/**`, `Diagnostics/**`, `Host/**`, `Testing/**`, `Rendering/**`, `DevTools/**`, and `Architecture/**` from compilation, plus four explicit Core test files.

The Release discovery check found:

| Test class | Discovered |
| --- | --- |
| `MissingApiTrackerTests` | yes |
| `RendererIpcMetadataTests` | yes |
| `CallbackFailureDiagnosticsTests` | yes |
| `DebugSiteExceptionSummaryTests` | yes |
| `FirstBlockerClassifierTests` | yes |
| `BrowserFormInteractionAcceptanceTests` | yes; 6 tests |
| `EventLoopTraceTests` | no |
| `RealSiteRenderDiagnostics` | no |
| `RendererChildLoopIoTests` | no |

The included `Scripting/CallbackFailureDiagnosticsTests.cs` and `Tooling/DebugSiteExceptionSummaryTests.cs` files activate only the first callback/export slice; no excluded tree was broadly re-enabled. The compile-removal patterns currently cover 246 C# files under excluded directory trees plus the four explicit Core files. Passing the normal test project therefore does not yet protect every diagnostic, render, host, or IPC path.

A combined parallel filter containing two independent FenJS runtime classes reproduced the existing shared-bootstrap isolation defect (`TypeError: Cannot read properties of undefined (reading 'prototype')`). Each timer class passes when run alone. FenJS test serialization remains verification-infrastructure work; the focused callback command above contains one FenJS runtime test and is deterministic.

## Test262 source of truth

Read `docs/test262_results.md`; do not rerun the full suite for status checks.

| Metric | Local batched snapshot |
| --- | --- |
| Snapshot | 2026-07-05 |
| Passed | 49,070 |
| Total | 53,198 |
| Pass rate | 92.24% |
| Categories at 100% | 1,490 / 1,767 |
| Categories below 100% | 277 |

Any future Test262 invocation must use the local `C:/Users/udayk/Videos/test262` checkout, a 2,000 ms per-test timeout, and the 30 second stall watchdog.

## WPT tracked aggregate

`Results/wpt_categories/_summary.json` is a local generated aggregate dated 2026-07-07. It is useful as a runner snapshot, not proof that every category is correctly implemented.

| Metric | Value |
| --- | --- |
| Total tests represented | 4,445 |
| Passed | 2,752 |
| Failed | 502 |
| Crashed | 369 |
| Timed out | 822 |
| Aggregate pass rate | 61.91% |
| Category errors | 17 |

The latest retained focused `dom/lists` summary at `Results/wpt_20260704_175911/wpt.summary.json` ended with exit code 1 and four OK statuses plus one ERROR. Selected WPT baselines must be rerun category-by-category from the local `C:/Users/udayk/Videos/wpt` checkout before using them as acceptance evidence.

## Real-site and diagnostic evidence

| Target | Evidence | Observed result | Status |
| --- | --- | --- | --- |
| Google | `logs/real-site/www.google.com/20260715T085915Z/` | Main page rendered; current lifecycle/event loop agree on complete/DCL/load; the earlier loading sample is explicitly transition-time/timed-out; 18 completed script executions; 0 direct script failures, 0 callback failures, 0 exceptions; `first_blocker.json` is `none`; input not automated | TESTED |
| Host collection iterator local fixture | `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_logs_fixtures_host_collection_iterator.html/20260715T085843Z/` | Async timer iterates a host-backed `HTMLCollection` and renders `passed:first,second`; 0 callback failures; 0 exceptions; complete lifecycle; `first_blocker.json` is `none` | REGRESSION_PROTECTED |
| Event/Promise failure local fixture | `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_logs_fixtures_event_promise_callback_failures.html/20260715T083850Z/` | Complete lifecycle; event-listener and unhandled-Promise records retain source/receiver/task/stack in order; event-loop, exceptions, trace, and summary agree at 2; `first_blocker.json` keeps both non-fatal | TESTED |
| Hot host-object `in` timer reduction | `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_logs_fixtures_host_object_in_timer.html/20260715T082004Z/` | Crosses the JIT threshold in a timer; renders `passed`; 0 callback failures; 0 exceptions; `first_blocker.json` is `none` | REGRESSION_PROTECTED |
| Throwing timer local fixture | `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_logs_fixtures_throwing_timer_callback.html/20260715T080257Z/` | Complete lifecycle; one typed post-load timer failure; `first_blocker.json` reports no boot blocker, records the callback as non-fatal, and marks all five interaction milestones unverified | TESTED |
| example.com control | `logs/real-site/example.com/20260713T074825Z/` | Complete lifecycle and screenshot, no failure | TESTED |
| `fen://performance` control | `logs/real-site/performance/20260714T102820Z/` | 518 boxes, screenshot, complete lifecycle | TESTED |

## Baselines still required

| Surface | Status | Required output |
| --- | --- | --- |
| Full `FenBrowser.Tests` included set | RESEARCHED | Fresh pass/fail list after resolving or avoiding active binary locks |
| `FenBrowser.Js.Tests` | RESEARCHED | Fresh complete result |
| Core-focused tests inside `FenBrowser.Tests` | RESEARCHED | Fresh discovered and executed result for active Core paths |
| html5lib | NOT_STARTED | Runner command, totals, failures, and local result bundle |
| Selected boot-critical WPT | RESEARCHED | `html`, `dom`, `fetch`, `cors`, `cookies`, `custom-elements`, `cssom`, and focused layout categories |
| WebDriver interaction smoke | TESTED | Local click/type/canceled-and-successful-submit behavior is regression-protected; screenshot/trace proof and the same sequence against Google remain required |
