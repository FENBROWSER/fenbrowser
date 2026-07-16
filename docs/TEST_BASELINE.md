# FenBrowser Test Baseline

Snapshot date: 2026-07-14; focused build/test and selected WPT evidence revalidated 2026-07-16. All paths and results are local. No conformance data in this file was fetched from the internet.

## Verification run in this audit

| Surface | Command | Result | Status |
| --- | --- | --- | --- |
| Tooling dependency graph, Release | `dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release -v minimal` | 0 errors, 498 warnings | TESTED |
| Diagnostic/process focused tests | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MissingApiTrackerTests|FullyQualifiedName~NavigationLifecycleTraceTests|FullyQualifiedName~RendererIpcMetadataTests|FullyQualifiedName~RendererIsolationPoliciesTests"` | 44 passed, 0 failed, 0 skipped | TESTED |
| Callback provenance/export focused tests | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CallbackFailureDiagnosticsTests|FullyQualifiedName~EngineLogSettingsTests.Flush_DrainsAcceptedEventsBeforeArtifactCopy|FullyQualifiedName~DebugSiteExceptionSummaryTests" --logger "console;verbosity=minimal"` | 9 passed, 0 failed, 0 skipped | TESTED |
| First-causal-blocker classifier fixtures | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FirstBlockerClassifierTests" --logger "console;verbosity=minimal"` | 12 passed, 0 failed, 0 skipped | TESTED |
| Lifecycle transition-detail and classifier slice | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~BrowserLifecycleDetailTests|FullyQualifiedName~NavigationLifecycleTrackerTests|FullyQualifiedName~FirstBlockerClassifierTests" --logger "console;verbosity=minimal"` | 18 passed, 0 failed, 0 skipped | TESTED |
| DOM host-collection iteration | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FenJsDomCollectionIterationTests" --logger "console;verbosity=minimal"` | 3 passed, 0 failed, 0 skipped; all 3 listed by `--list-tests` | REGRESSION_PROTECTED |
| Form, Tooling, and render-generation interaction acceptance | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CustomHtmlEngineNavigationGenerationTests|FullyQualifiedName~BrowserFormInteractionAcceptanceTests|FullyQualifiedName~DebugSiteInteractionRunnerTests" --logger "console;verbosity=minimal"` | 14 passed, 0 failed, 0 skipped | REGRESSION_PROTECTED |
| WPT result classification and new-window focus | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WptToolRunnerRawLogTests|FullyQualifiedName~HostBrowserDriverNewWindowTests|FullyQualifiedName~WebDriverClickWithoutInteractablePoint_Throws" --logger "console;verbosity=minimal"` | 8 passed, 0 failed, 0 skipped; all 8 listed by `--list-tests` | REGRESSION_PROTECTED |
| DOMTokenList value binding and ordered-set parsing | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DomTokenListValueBindingTests" --logger "console;verbosity=minimal"` | 2 passed, 0 failed, 0 skipped; both listed by `--list-tests` | REGRESSION_PROTECTED |
| FenJS checkbox click activation | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FenJsCheckboxActivationTests" --logger "console;verbosity=minimal"` | 4 passed, 0 failed, 0 skipped; all 4 listed by `--list-tests` | REGRESSION_PROTECTED |
| WebIDL manual-binding inventory | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WebIdlInventoryRunnerTests|FullyQualifiedName~WebIdlBindingGeneratorTests" --logger "console;verbosity=minimal"` | 2 passed, 0 failed, 0 skipped; inventory fixture listed by `--list-tests` | TESTED |
| FenJS host lifetime measurement | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FenJsHostLifetimeMeasurementTests" --logger "console;verbosity=detailed"` | 2 passed, 0 failed, 0 skipped; both listed; six resets remain 6 live handles at baseline and 38 with 32 retained nodes | TESTED |
| Brokered renderer startup components and policy | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~RendererIsolationPoliciesTests|FullyQualifiedName~BrokeredRendererProcessAcceptanceTests|FullyQualifiedName~HostExecutablePathResolverTests|FullyQualifiedName~RendererChildEnvironmentTests|FullyQualifiedName~WindowsAppContainerEnvironmentTests" --logger "console;verbosity=minimal"` | 43 passed, 0 failed, 1 skipped; the skip is `BLOCK-PROC-002`, not accepted brokered startup | TESTED |
| Debug-site supplemental artifact contract | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DebugSiteArtifactContractTests|FullyQualifiedName~DebugSiteExceptionSummaryTests" --logger "console;verbosity=minimal"` | 2 passed, 0 failed, 0 skipped; artifact-contract test listed by `--list-tests` | REGRESSION_PROTECTED |
| Missing-API schema-v2 classification and rich bundle export | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MissingApiTrackerTests|FullyQualifiedName~DebugSiteMissingApiClassificationTests" --logger "console;verbosity=minimal"` | 9 passed, 0 failed, 0 skipped; all 9 listed by `--list-tests` | REGRESSION_PROTECTED |
| Host process-isolation surface, Release | `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Release --no-restore --verbosity quiet` | 496 warnings, 0 errors | TESTED |
| FenEngine, Release | `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Release --no-restore -v:minimal` | 0 warnings, 0 errors | TESTED |
| Tooling, Release | `dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-restore --verbosity quiet` | 2 warnings, 0 errors | TESTED |
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
| `MissingApiTrackerTests` | yes; 5 tests |
| `RendererIpcMetadataTests` | yes |
| `CallbackFailureDiagnosticsTests` | yes |
| `DebugSiteExceptionSummaryTests` | yes |
| `FirstBlockerClassifierTests` | yes |
| `BrowserFormInteractionAcceptanceTests` | yes; 8 tests |
| `DebugSiteInteractionRunnerTests` | yes; 5 tests |
| `CustomHtmlEngineNavigationGenerationTests` | yes; 1 test |
| `WptToolRunnerRawLogTests` | yes; 6 cases |
| `HostBrowserDriverNewWindowTests` | yes; 1 test |
| `FenJsCheckboxActivationTests` | yes; 4 tests |
| `WebIdlInventoryRunnerTests` | yes; 1 test |
| `FenJsHostLifetimeMeasurementTests` | yes; 2 tests |
| `BrokeredRendererProcessAcceptanceTests` | yes; 1 explicitly blocked/skipped test |
| `HostExecutablePathResolverTests` | yes; 1 test |
| `RendererChildEnvironmentTests` | yes; 1 test |
| `WindowsAppContainerEnvironmentTests` | yes; 1 test |
| `DebugSiteArtifactContractTests` | yes; 1 test |
| `DebugSiteMissingApiClassificationTests` | yes; 1 test |
| `EventLoopTraceTests` | no |
| `RealSiteRenderDiagnostics` | no |
| `RendererChildLoopIoTests` | no |

The included `Scripting/CallbackFailureDiagnosticsTests.cs` and `Tooling/DebugSiteExceptionSummaryTests.cs` files activate only the first callback/export slice; no excluded tree was broadly re-enabled. The compile-removal patterns currently cover 246 C# files under excluded directory trees plus the four explicit Core files. Passing the normal test project therefore does not yet protect every diagnostic, render, host, or IPC path.

The brokered acceptance was red before its explicit blocker annotation: the initial test-runner launch reported `renderer-startup-failed`; after resolving the FenBrowser apphost and preserving the Unicode child environment, strict AppContainer launch reports native error 2 for the development runtime path. A temporary exact-SID ACL experiment removed the immediate spawn error but timed out before full acceptance. No unsandboxed fallback was used, and all temporary ACEs were removed and verified absent.

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

The current selected browser-integration matrix uses local WPT revision `88152b842c3f60c2a5f95e0106ded4a375f710b0` and FenBrowser `76bfb83290b65dab532acc81560236523ab64995`, Release, one process, and the default in-process mode. `Results/wpt/selected/20260716_checkbox_clean_run1/` and `Results/wpt/selected/20260716_checkbox_clean_run2/` each completed three starts and three ends in 11.70 s and 11.59 s respectively. Both classify `DOMTokenList-stringifier.html`, `DOMTokenList-value.html`, and `checkbox-click-events.html` as Pass, exit 0, and contain zero unexpected tests, unexpected subtests, crashes, timeouts, WebDriver failures, harness failures, or category ambiguity.

## Real-site and diagnostic evidence

| Target | Evidence | Observed result | Status |
| --- | --- | --- | --- |
| Google interaction | `logs/real-site/www.google.com/20260715T103925Z/` | At 1280x800, hit-tested `TEXTAREA#APjFqb`, focused it, accepted nonce `fen715j`, emitted keyboard/input/change/blur plus submit-control pointer/click and form submit records, completed a 200 GET `/search` navigation, then followed Google's delayed redirect to a genuine HTTP 429 `/sorry/` challenge. Lifecycle, active DOM/rendered text, and the visibly different after screenshot all describe navigation 3's challenge document; `first_blocker.json` reports `none`, callback failures are 0, and exceptions are 0. No challenge bypass was attempted. | REGRESSION_PROTECTED |
| Local form Tooling interaction | `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_interaction_result.html_q_fen-local-20260715-4_source_local-fixture_include_yes_submitter_go/20260715T094724Z/` | Hit-tested click acquired focus, accepted 20 characters, emitted 233 bounded event records, submitted successful controls, reached terminal `result.html`, captured before/after screenshots, and completed all 19 blocker milestones with result `none` | REGRESSION_PROTECTED |
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
| Selected boot-critical WPT | TESTED | Initial DOM/forms/WebDriver matrix is repeatable and exactly classified; expand with focused lifecycle, fetch/CORS, cookies, custom-elements, CSSOM/geometry, and layout files |
| WebDriver interaction smoke | REGRESSION_PROTECTED | Local click/type/canceled-and-successful-submit, current-layout hit testing, compound selector lookup, chained-navigation settlement, Tooling viewport/artifact ownership, and stale-render generation rejection are covered; Google focus/type/submit/request/terminal-frame agreement is captured |
