# FenBrowser Real-Site Tracker

Snapshot date: 2026-07-15. Only evidence present in the current workspace is treated as current.

## Minimal smoke matrix

| ID | Category | Target | URL | Login | Current status | Acceptance focus |
| --- | --- | --- | --- | --- | --- | --- |
| SITE-SEARCH-001 | Search | Google | `https://www.google.com/` | no | TESTED | load, type, submit, result navigation |
| SITE-WIKI-001 | Documentation/wiki | Wikipedia | `https://en.wikipedia.org/` | no | NOT_STARTED | article layout, links, scrolling |
| SITE-GITHUB-001 | GitHub-like app | GitHub | `https://github.com/` | no | RESEARCHED | fresh boot, nav/input, visual fidelity |
| SITE-SOCIAL-001 | Social/media SPA | X | `https://x.com/` | no for landing | NOT_STARTED | bundle fetch, framework boot, scrolling |
| SITE-VIDEO-001 | Video | YouTube | `https://www.youtube.com/` | no | NOT_STARTED | custom elements, shadow DOM, media shell |
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

Current visible result: The 1280x800 before screenshot contains the Google logo, search control, buttons, language links, navigation, and footer. The after screenshot shows nonce `fen715f` in the focused search control and the focused submit button. The interaction then recorded a successful 200 GET navigation to `/search?q=fen715f...`; the after screenshot does not yet prove the search-results document rendered.

Expected visible result: The same main UI plus verified focus, typing, submit/click navigation, and visible network/input trace records.

First fatal console error: None captured.

First remaining runtime error: None captured. The attributed `script-5` / `k0c` rejection was a `for...of` over `document.getElementsByTagName('img')`: FenJS ignored host-object prototype iterators and the manual `HTMLCollection` surface lacked `Symbol.iterator`. The general host collection iterator path is now regression-protected; the earlier eight `setTimeout` host-object failures also remain fixed.

First fatal network error: None confirmed. The only failed request is a `data:image/gif` URI, which is a capture-classification defect rather than an HTTP failure.

First missing API: The summary reports `Element.closure_listenable_498696`; this is a site-expando candidate, not a confirmed Web API. Standards candidates include `Document.compareDocumentPosition`, `Location.toString`, and `CharacterData.childNodes`; none is confirmed fatal.

First layout blocker: None captured. There are 34 zero-area boxes, but the main UI is visible.

Script loading status: Completed. 14 script elements, 18 execution completions, 0 execution failures.

DOMContentLoaded fired: Yes.

Load fired: Yes.

Main framework detected: Google Closure-style property names are present; this is an inference from `closure_*` and `$goog_Thenable`, not a confirmed framework detector result.

Likely failure bucket: Diagnostics/lifecycle evidence for the post-submit document: interaction succeeds, but the final classifier reports `insufficient-evidence` for `RequiredResourcesFetched` and the after screenshot remains on the submitted homepage state. F/E remain candidates only for unclassified missing-property observations.

Confirmed failure bucket: The previous E/G defect was a baseline-JIT `in`-operator divergence and is regression-protected. Google focus, typing, submit activation, and result navigation are now verified. Result-page visual settlement and consistent post-navigation blocker classification remain unverified.

Minimal reproduction: Passive: `dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- debug-site https://www.google.com/ 20000`. Interaction: use `debug-site-interact` with selectors confirmed from the fresh Google DOM; do not hardcode them into engine behavior.

Engine subsystem owner: FenJS iterator/host-prototype dispatch and the manual DOM collection surface for the resolved callback defect; FenEngine layout/hit testing and WebDriver lookup for the resolved interaction defects; Tooling/classifier lifecycle capture for the remaining post-navigation evidence gap.

Fix task: Callback attribution, both attributed host-object defects, transition-sample lifecycle normalization, compiled local form interaction acceptance, current-layout WebDriver geometry, viewport-consistent hit testing, full selector lookup, and Tooling-driven Google focus/type/submit/navigation evidence are complete. Next reduce the result-page screenshot/classifier disagreement.

Regression test: REGRESSION_PROTECTED. `FenJsDomCollectionIterationTests` protects the last callback defect. Eight discovered `BrowserFormInteractionAcceptanceTests` protect ordinary hit-tested click, current-layout/pointer-events geometry, compound selector lookup, focus, typing, input/change order, canceled `beforeinput`, canceled submit, live checkedness/activation, successful-control GET navigation, and non-interactable rejection. Three discovered `DebugSiteInteractionRunnerTests` protect bounded event export, value privacy fields, terminal navigation waiting, local result-DOM agreement, and the fixed 1280x800 diagnostic viewport.

Evidence: Google boot: `logs/real-site/www.google.com/20260715T085915Z/`. Google interaction: `logs/real-site/www.google.com/20260715T101621Z/`. Local interaction: `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_interaction_result.html_q_fen-local-20260715-4_source_local-fixture_include_yes_submitter_go/20260715T094724Z/`.

Status: TESTED for load/render plus Google focus/type/submit/navigation; result-page visual settlement and post-navigation classifier agreement remain RESEARCHED.
