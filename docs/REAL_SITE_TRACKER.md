# FenBrowser Real-Site Tracker

Snapshot date: 2026-10-01. Only evidence present in the current workspace is treated as current.

## Minimal smoke matrix

| ID | Category | Target | URL | Login | Current status | Acceptance focus |
| --- | --- | --- | --- | --- | --- | --- |
| SITE-SEARCH-001 | Search | Google | `https://www.google.com/` | no | TESTED | load, type, submit, result navigation |
| SITE-WIKI-001 | Documentation/wiki | Wikipedia | `https://en.wikipedia.org/` | no | RESEARCHED | article layout, links, scrolling |
| SITE-GITHUB-001 | GitHub-like app | GitHub | `https://github.com/` | no | RESEARCHED | fresh boot, nav/input, visual fidelity |
| SITE-SOCIAL-001 | Social/media SPA | X | `https://x.com/` | no for landing | NOT_STARTED | bundle fetch, framework boot, scrolling |
| SITE-VIDEO-001 | Video | YouTube | `https://www.youtube.com/` | no | PARTIAL | player shell, media pipeline, googlevideo 403 |
| SITE-COMMERCE-001 | Ecommerce | Amazon | `https://www.amazon.com/` | no | NOT_STARTED | redirects/cookies, dense layout, search input |
| SITE-MAIL-001 | Webmail-like | Gmail | `https://mail.google.com/` | yes | NOT_STARTED | unauthenticated shell only unless credentials are supplied manually |
| SITE-DASHBOARD-001 | Dashboard SPA | Grafana demo | `https://demo.grafana.org/` | target-dependent | NOT_STARTED | module boot, fetch, grid, canvas/SVG |
| SITE-NEWS-001 | News | Hacker News | `https://news.ycombinator.com/` | no | NOT_STARTED | table layout, links, scrolling |
| SITE-FORM-001 | Banking/form-heavy safety fixture | Planned deterministic fixture | `FenBrowser.Tests/Fixtures/real-site/form-heavy.html` | no | NOT_STARTED | focus, labels, validation, typing, submit; no real bank automation |
| SITE-CSS-001 | Heavy CSS | CSS Zen Garden | `https://www.csszengarden.com/` | no | NOT_STARTED | cascade, fonts, backgrounds, responsive layout |
| SITE-JS-001 | Heavy JavaScript app | React TodoMVC | `https://todomvc.com/examples/react/dist/` | no | NOT_STARTED | framework boot, events, storage, mutation |

Controls that do not replace a real-site row:

- `example.com`: TESTED at `logs/real-site/example.com/20260713T074825Z/`.
- `fen://performance`: TESTED at `logs/real-site/performance/20260714T102820Z/`.

## SITE-SEARCH-001 triage

Site: Google

URL: `https://www.google.com/`

Current visible result: The 1280x800 before screenshot contains the Google logo, search control, buttons, language links, navigation, and footer. The interaction accepted nonce `fen715j`, submitted a 200 GET `/search` request, and then followed Google's delayed redirect to a genuine HTTP 429 `/sorry/` challenge. Final lifecycle, active DOM/rendered text, and the visibly different after screenshot all describe that terminal challenge document.

Expected visible result: The same main UI plus verified focus, typing, submit/click navigation, and visible network/input trace records.

First fatal console error: None captured.

First remaining runtime error: None captured. The attributed `script-5` / `k0c` rejection was a `for...of` over `document.getElementsByTagName('img')`: FenJS ignored host-object prototype iterators and the manual `HTMLCollection` surface lacked `Symbol.iterator`. The general host collection iterator path is now regression-protected; the earlier eight `setTimeout` host-object failures also remain fixed.

First fatal network error: Google returned HTTP 429 and redirected the submitted search to its `/sorry/` challenge. This is an external security response, not an engine assertion failure, and the run did not attempt to bypass it.

First missing API: The fresh passive summary reports `Navigator.msPointerEnabled`, classified `LEGACY_PROBE`. The 25 retained records comprise 9 `STANDARD_API`, 11 `SITE_EXPANDO`, 2 `WRONG_RECEIVER`, 1 `LEGACY_PROBE`, and 2 `UNCLASSIFIED`; none is confirmed fatal. Six Closure/Thenable protocol records carry boolean function-prototype-marker evidence and five Closure bookkeeping records carry ordinary `[READ, WRITE]` evidence; neither classification uses a name rule.

First layout blocker: None captured. There are 34 zero-area boxes, but the main UI is visible.

Script loading status: Completed. 14 script elements, 18 execution completions, 0 execution failures.

DOMContentLoaded fired: Yes.

Load fired: Yes.

Main framework detected: Google Closure-style property names are present; this is an inference from `closure_*` and `$goog_Thenable`, not a confirmed framework detector result.

Likely failure bucket: No remaining interaction or render-publication blocker in the current run. F/E remain candidates only for unclassified missing-property observations.

Confirmed failure bucket: The previous E/G defect was a baseline-JIT `in`-operator divergence and is regression-protected. The stale post-navigation document was older CSS/script work publishing after a newer render generation began; generation-aware DOM/style/telemetry publication now protects the terminal document. Google focus, typing, submit activation, the 200 search request, delayed redirect, terminal challenge navigation, and terminal frame all agree.

Minimal reproduction: Passive: `dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- debug-site https://www.google.com/ 20000`. Interaction: use `debug-site-interact` with selectors confirmed from the fresh Google DOM; do not hardcode them into engine behavior.

Engine subsystem owner: FenJS iterator/host-prototype dispatch and the manual DOM collection surface for the resolved callback defect; FenEngine layout/hit testing, WebDriver lookup, and render-generation publication for the resolved interaction defects.

Fix task: Callback attribution, both attributed host-object defects, transition-sample lifecycle normalization, compiled local form interaction acceptance, current-layout WebDriver geometry, viewport-consistent hit testing, full selector lookup, terminal-generation quieting, stale render-publication rejection, and Tooling-driven Google focus/type/submit/navigation/frame evidence are complete.

Regression test: REGRESSION_PROTECTED. `FenJsDomCollectionIterationTests` protects the last callback defect. Eight discovered `BrowserFormInteractionAcceptanceTests` protect ordinary hit-tested click, current-layout/pointer-events geometry, compound selector lookup, focus, typing, input/change order, canceled `beforeinput`, canceled submit, live checkedness/activation, successful-control GET navigation, and non-interactable rejection. Five discovered `DebugSiteInteractionRunnerTests` protect bounded event export, value privacy fields, chained terminal-navigation quieting, local result-DOM agreement, the fixed 1280x800 diagnostic viewport, and Tooling-owned screenshot artifacts. The discovered `CustomHtmlEngineNavigationGenerationTests` reduction blocks an older delayed CSS render from replacing the newer DOM, styles, or telemetry.

Evidence: Google classified passive run: `logs/real-site/www.google.com/20260716T120932Z/`. Google interaction: `logs/real-site/www.google.com/20260715T103925Z/`. Local first-blocker/artifact control: `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_stringifier_partial_interface.html/20260716T120850Z/`. Local operation-kind reduction: `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_operation_kinds.html/20260716T114816Z/`.

Status: REGRESSION_PROTECTED for load/render plus Google focus/type/submit/request/challenge navigation and terminal active-DOM/frame agreement. Normal result rendering remains externally blocked by Google's HTTP 429 challenge, not bypassed.

## SITE-WIKI-001 triage

Site: Wikipedia

URL: `https://en.wikipedia.org/` (final: `/wiki/Main_Page` after redirect)

Current visible result: Full boot. 4021 DOM nodes, 2350 styled nodes, 887 layout boxes, screenshot captured, navigation lifecycle Complete, DOMContentLoaded and load both fired, 72/72 network requests succeeded, scripts executed with zero fetch or execution failures.

Expected visible result: Same main UI plus zero fatal engine exceptions during boot and deferred callbacks.

First fatal console error: (fixed this cycle) JsEngineFatalException: Stale heap handle — FenJS major GC swept event facades, timer closures, and MutationObserver record arrays that host code held across the large-stack worker marshal; 14 occurrences killed DCL/load listeners and the document readyState probe. Root-caused and fixed by generation-tagged pin scopes in FenJsBrowserScriptEngine (Volume III 2.116).

First fatal network error: None.

First missing API: PerformanceObserver is not defined from MediaWiki experiment bootstrapping (suggestionMode.js) — 4 promise rejections, non-fatal; tracked as BIND-002.

First layout blocker: None captured. 21 zero-area boxes; main UI visible.

Script loading status: Completed. 5 discovered, 4 eligible, 10 executions, 0 failures.

DOMContentLoaded fired: Yes.

Load fired: Yes.

Main framework detected: MediaWiki ResourceLoader + jQuery (jquery, oojs, mediawiki.base modules).

Likely failure bucket: Resolved G (event loop / GC rooting). Remaining candidates: F/E for PerformanceObserver; D/E for Function.prototype.apply rejecting array-like host objects inside jQuery find (4 timer-callback TypeErrors).

Confirmed failure bucket: Stale heap handle class confirmed fixed by before/after bundles below; first_blocker.json now reports Result=none.

Minimal reproduction: `dotnet run --project FenBrowser.Tooling -c Release -- debug-site https://en.wikipedia.org/ 20000`

Engine subsystem owner: FenJS heap/root-source integration (FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs).

Fix task: Pin-scope rooting landed this cycle. Follow-ups: BIND-002 (PerformanceObserver), JS-001 (apply array-like coercion).

Regression test: Pending — deterministic GC-stress regressions are blocked on the in-flight stress-mode viability work (interpreter construction pinning + SetStressModeForDiagnostics hook); real-site A/B bundles serve as interim evidence.

Evidence: Before bundle logs/real-site/en.wikipedia.org/20260821T184115Z/ (22 callback failures, 14 stale-handle fatals, first_blocker insufficient-evidence). After bundle logs/real-site/en.wikipedia.org/20260821T191613Z/ (8 callback failures, 0 stale-handle fatals, first_blocker none).

Status: RESEARCHED for boot-level acceptance (document loads, scripts execute, lifecycle events fire, layout/paint/screenshot captured, no renderer crash); interaction milestones not yet attempted.
## SITE-VIDEO-001 triage

Site: YouTube

URL: `https://www.youtube.com/watch?v=jNQXAC9IVRw` (and `https://www.youtube.com/` for the home shell)

Current visible result: The player shell renders. `div#movie_player.html5-video-player` is laid out at 830x467 with the red play button, the video title, and the search control visible in a 1280x800 screenshot. `http://www.youtube.com/` renders the signed-out home shell unchanged ("Try searching to get started" is YouTube's own no-cookies empty state, not an engine defect). The media engine itself is proven working: a `<video src>` page served over HTTP reports readyState 4, duration 52.21, videoWidth 854, videoHeight 480, `play()` resolves, `currentTime` advances, and decoded frames are painted.

Expected visible result: Same player shell plus advancing video frames and audio.

First fatal console error: (fixed this cycle) `Uncaught Error: Regular expression backtracking budget exceeded (pattern /^((http(s)?):)?\/\/((((lh[3-6]...|video\.google\.com|...)[.]?(:[0-9]+)?/|...)/ ...` thrown from `create` in `/s/player/8ab5c328/player_es6.vflset/en_US/base.js:8816` during `playerBootstrap`. Google's host allow-list regex has a bounded host-label run repeated in front of a large alternation; against a 30-character URL the backtracking VM re-explored the same (pc, position) thread states exponentially and aborted the page boot. Root-caused in RegexVM thread-state memoization (26c75c2d): the visited set was cleared on every stack pop, so it only ever covered one straight-line run of instructions.

First fatal network error: All six `GET https://rr1---sn-ntqe6nee.googlevideo.com/videoplayback?...itag=18&mime=video/mp4&c=WEB...` requests returned **HTTP 403** (empty body, `Server: gvs 1.0`, `Content-Length: 0`). This is an external refusal, not an engine assertion: the same URL fetched with `curl` and a plausible Chrome 140 UA also returns 403, with and without the `n=` throttling parameter, and a plain-HTTP `/youtubei/v1/player` call returns `playabilityStatus: UNPLAYABLE / "Video unavailable"` for both the engine's UA and a real Chrome UA. Not attempted: any bypass.

First missing API: `Animation.cancel` (472 retained records, mostly Polymer `HTMLElement.rootPath` / `importPath` / `$` / `$$` expandos and `DocumentFragment` host-property reads that Polymer sets on itself). None confirmed fatal; zero uncaught exceptions in the current run.

First layout blocker: None from the blocker classifier. `div.html5-video-container` is laid out 830x0 with the video element at y=-373 (above the viewport) while `div#movie_player` around it is 830x467. NOT yet root-caused: a minimized fixture reproducing that shape (position:relative container + position:absolute top/left/width:100%/height:100% video) lays out spec-correctly at 830x0, both for a `<div>` and for a `<video>`, so the used size on the real page is being set from somewhere the dumps do not yet show - most likely inline styles written by the player's own sizing code. Recorded as the next lead, not as a fix.

Script loading status: Completed. 51 discovered, 55 executions, 0 failures, 0 fetch failures.

DOMContentLoaded fired: Yes.

Load fired: Yes.

Main framework detected: Polymer 2 (Kevlar) + the player's own imperative DOM, with `custom-elements-es5-adapter` and `webcomponents-sd` polyfills.

Likely failure bucket: Resolved D (JavaScript language / regex VM). Network bucket B for the googlevideo 403. I bucket for the video geometry.

Confirmed failure bucket: D confirmed and fixed (26c75c2d). B confirmed as an external server response with an out-of-engine reproduction.

Minimal reproduction: `dotnet run --project FenBrowser.Tooling -c Release --no-build -- debug-site "https://www.youtube.com/watch?v=jNQXAC9IVRw" 25000`

Engine subsystem owner: FenJs regex VM (FenBrowser.Js/Regex/RegexVM.cs) for the fatal error; googlevideo CDN for the 403.

Fix task: Done for the boot blocker. Follow-ups: (1) root-cause the `div.html5-video-container` 0-height / video at y=-373 geometry from the live page's inline styles; (2) ECMA-262 22.2.2.13 RepeatMatcher empty-iteration guard is still absent, so `/(a?b??)*/` against "ab" reports the wrong match (`built-ins/RegExp/nullable-quantifier.js`) - a real spec gap that is not on the YouTube path.

Regression test: FenBrowser.Js.Tests/RegexExecutionLimitTests - `MemoStopsRepeatedLabelRunsAgainstAnAlternation` (minimized host-allow-list shape), `MemoStopsRedosShapedSearches`, and the two budget tests retargeted at a backreference pattern, which is the shape the memo cannot cover and where the budget is still the protection.

Evidence: Before bundle logs/real-site/www.youtube.com/20261001T044037Z/ (1 uncaught regex-budget error, 0 console messages otherwise, rendered text 2352 chars of which the error dominated, layout boxes 70). After bundle logs/real-site/www.youtube.com/20261001T072644Z/ (0 exceptions, 0 fatal console errors, rendered text "Me at the zoo / Play / Search", layout boxes 105, first_blocker none confidence 1).

Status: PARTIAL. The page boots, the player renders, and the media pipeline decodes and paints video. Playback itself is blocked by an HTTP 403 from googlevideo that reproduces outside the engine.