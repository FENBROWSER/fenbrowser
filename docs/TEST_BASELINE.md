# FenBrowser Test Baseline

Snapshot date: 2026-07-14. All paths and results are local. No conformance data in this file was fetched from the internet.

## Verification run in this audit

| Surface | Command | Result | Status |
| --- | --- | --- | --- |
| Tooling dependency graph, Release | `dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release -v minimal` | 0 errors, 0 warnings | TESTED |
| Diagnostic/process focused tests | `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MissingApiTrackerTests|FullyQualifiedName~NavigationLifecycleTraceTests|FullyQualifiedName~RendererIpcMetadataTests|FullyQualifiedName~RendererIsolationPoliciesTests"` | 44 passed, 0 failed, 0 skipped | TESTED |
| Tooling dependency graph, Debug | Same build in Debug | Reached project compilation, then failed copying Host dependencies because Visual Studio and a running FenBrowser.Host locked Debug DLLs | RESEARCHED |

The Debug result is an environment lock, not a source compilation failure. The active processes were not terminated because they belong to the user's live workspace session.

## Test discovery gap

`FenBrowser.Tests/FenBrowser.Tests.csproj` removes `Engine/**`, `DOM/**`, `WebAPIs/**`, `Workers/**`, `Interaction/**`, `Integration/**`, `Diagnostics/**`, `Host/**`, `Rendering/**`, and `Architecture/**` from compilation.

The Release discovery check found:

| Test class | Discovered |
| --- | --- |
| `MissingApiTrackerTests` | yes |
| `RendererIpcMetadataTests` | yes |
| `EventLoopTraceTests` | no |
| `RealSiteRenderDiagnostics` | no |
| `RendererChildLoopIoTests` | no |

Passing the normal test project therefore does not currently protect every diagnostic, render, host, or IPC path.

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
| Google | `logs/real-site/www.google.com/20260714T075906Z/` | Main page rendered; DCL/load true; 17 completed script executions; input not automated | TESTED |
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
| WebDriver interaction smoke | NOT_STARTED | Click/type/submit/screenshot proof against Google or a deterministic local reduction |
