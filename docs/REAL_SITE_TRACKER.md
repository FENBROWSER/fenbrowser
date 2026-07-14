# FenBrowser Real-Site Tracker

Snapshot date: 2026-07-14. Only evidence present in the current workspace is treated as current.

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

Current visible result: The 1280x800 screenshot contains the Google logo, search control, buttons, language links, navigation, and footer. The document, script, event-loop, layout, paint, and raster stages completed.

Expected visible result: The same main UI plus verified focus, typing, submit/click navigation, and visible network/input trace records.

First fatal console error: None captured.

First remaining runtime error: Eight `setTimeout` callbacks fail after DOMContentLoaded. The snapshot's retained error is `TypeError: Cannot use a host object where a JS object is expected.` The bundle does not preserve which source/callback produced each occurrence, and load still fires, so this is not yet classified as fatal.

First fatal network error: None confirmed. The only failed request is a `data:image/gif` URI, which is a capture-classification defect rather than an HTTP failure.

First missing API: The summary reports `Element.closure_listenable_498696`; this is a site-expando candidate, not a confirmed Web API. Standards candidates include `Document.compareDocumentPosition`, `Location.toString`, and `CharacterData.childNodes`; none is confirmed fatal.

First layout blocker: None captured. There are 31 zero-area boxes, but the main UI is visible.

Script loading status: Completed. 13 script elements, 13 eligible, 6 fetch starts/completions, 17 execution starts/completions, 0 failures.

DOMContentLoaded fired: Yes, at `2026-07-14T07:59:06.0098103Z`.

Load fired: Yes, at `2026-07-14T07:59:06.7861094Z`.

Main framework detected: Google Closure-style property names are present; this is an inference from `closure_*` and `$goog_Thenable`, not a confirmed framework detector result.

Likely failure bucket: E and G for host-object/JS-value interop during timer callbacks; L for diagnostic omission and missing interaction proof.

Confirmed failure bucket: G for eight failed timer callbacks and L for diagnostic attribution: `event_loop.json` retains one error, `exceptions.json` is empty, and the summary does not surface the failures. Bucket E remains a root-cause hypothesis until source/receiver attribution is preserved.

Minimal reproduction: `dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- debug-site https://www.google.com/ 20000`.

Engine subsystem owner: FenEngine event-loop callback execution and FenJS host-object conversion, plus the Tooling bundle builder for lost attribution.

Fix task: `TRACE-001`; then create a narrow host-object interop fix task from the attributed callback. `SITE-001` independently verifies interaction acceptance.

Regression test: NOT_STARTED. It must use an included test surface and a deterministic local timer-failure fixture before the live rerun.

Evidence: `logs/real-site/www.google.com/20260714T075906Z/summary.md`, `event_loop.json`, `missing_apis.json`, `network.json`, `style_layout.json`, and `screenshot.png`.

Status: TESTED for load/render; interaction acceptance remains RESEARCHED.
