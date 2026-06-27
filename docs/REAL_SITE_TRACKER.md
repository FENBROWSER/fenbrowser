# FenBrowser — Real-Site Tracker

> Status of important websites. Updated as evidence is collected.
> Format per PLAN.MD § "Real-Site Smoke Testing".

## Smoke-Test Matrix (Minimum 12 Categories)

| # | Site | Category | URL | Status | Evidence |
|---|------|----------|-----|--------|----------|
| 1 | Google | Search | https://www.google.com | NOT_STARTED | — |
| 2 | Wikipedia | Documentation/wiki | https://en.wikipedia.org | NOT_STARTED | — |
| 3 | GitHub | GitHub-like app | https://github.com | 🔴 Broken | See below |
| 4 | x.com | Social SPA | https://x.com | 🔴 Severely broken | See below |
| 5 | YouTube | Video page | https://www.youtube.com | NOT_STARTED | — |
| 6 | Amazon | Ecommerce | https://www.amazon.com | NOT_STARTED | — |
| 7 | Gmail | Webmail | https://mail.google.com | NOT_STARTED | — |
| 8 | React docs | Documentation SPA | https://react.dev | 🟠 Partial | See below |
| 9 | Hacker News | News | https://news.ycombinator.com | 🟡 Mostly works | See below |
| 10 | Banking | Form-heavy | TBD | NOT_STARTED | — |
| 11 | CSS Zen Garden | CSS layout | http://www.csszengarden.com | NOT_STARTED | — |
| 12 | TodoMVC (React) | JS app | TBD | NOT_STARTED | — |

---

## Detailed Site Reports

### github.com

| Field | Value |
|-------|-------|
| Site ID | github-001 |
| URL | https://github.com |
| Category | GitHub-like app (3) |
| Login required | No |
| Expected behavior | Full GitHub homepage with nav, search, feed |
| Current behavior | Page loads, DCL/load fire, 69/84 scripts execute, but 7 scripts fail (June 27 trace). Major layout gaps: 102 elements with content but zero height — inline/flex formatting context issue. 6 missing APIs detected (Document.tagName 357x, Element.content, Document.baseURI, etc.) |
| Screenshot | `logs/real-site/github.com/20260627T153353Z/screenshot.png` (2026-06-27) |
| Console errors | 0 console messages captured |
| Network errors | 0 failed requests (121 total) |
| JS exceptions | 7 scripts fail: 3 runtime errors (HostObject binding, null .readyState), 2 parser errors (/ regex ambiguity), 1 undefined.replace, 1 null.readyState |
| Missing APIs | `Document.tagName` (357x), `Document.nodeType` (4x), `Element.content` (2x), `Document.baseURI`, `Element.name`, `Element.prepend` — all from EngineCapabilities tracking |
| Layout/rendering bugs | 1920/1920 elements styled, but layout boxes=0 in diagnostic render (screenshot capture uses separate SkiaDomRenderer that doesn't populate boxes — known diag artifact) |
| Input/event bugs | Not tested |
| Storage/cookie bugs | Not tested |
| Crash/hang | No crash |
| Likely root cause | **Script failures** (4 runtime + 2 parser) block framework initialization; **inline/flex zero-height** causes visual gaps. First fatal: missing DOM APIs + host-object binding gaps |
| Confirmed root cause | Parser: `static` in destructuring FIXED (commit 1f126d66). Remaining: `/` regex-vs-division lexer ambiguity; HostObject call-target resolution; `Document.readyState` null access |
| Linked engine tasks | T4.2, T4.3a (done), T4.3b, T4.4 |
| Tests added | None yet |
| Status | DIAGNOSED |
| Evidence | Full trace bundle at `logs/real-site/github.com/20260627T153353Z/` (20 artifacts) |

### x.com

| Field | Value |
|-------|-------|
| Site ID | x-001 |
| URL | https://x.com |
| Category | Social SPA (4) |
| Login required | No (shows landing page) |
| Expected behavior | X.com landing page with sign-up prompt |
| Current behavior | Barely loads — only 81 elements, 45 display:none, 10 with layout rects |
| Screenshot | `real_site_render_xcom.png` (2026-06-23) — mostly white |
| Console errors | Not captured |
| Network errors | Not captured (external bundles likely not fetched) |
| JS exceptions | Not captured |
| Missing APIs | Not tracked |
| Layout/rendering bugs | 26/81 elements missing rects; only 10/81 get layout rects |
| Input/event bugs | Not tested |
| Storage/cookie bugs | Not tested |
| Crash/hang | No crash |
| Likely root cause | **Script loading failure** — external JS bundles from abs.twimg.com not fetched/executed; page stuck at "Loading…" shell state. Previously: cross-thread deadlock in BindFenJsDomContext (FIXED 2026-06-05) |
| Confirmed root cause | Not confirmed for current state |
| Linked engine tasks | T4.1 |
| Tests added | None yet |
| Status | RESEARCHED |
| Evidence | Layout dump at `real_site_render_xcom.diag.txt` (2026-06-23) |

### react.dev

| Field | Value |
|-------|-------|
| Site ID | react-001 |
| URL | https://react.dev |
| Category | Documentation SPA (8) |
| Login required | No |
| Expected behavior | React documentation homepage |
| Current behavior | Renders mostly but 497/1843 elements (27%) missing layout rects |
| Screenshot | `real_site_render_reactdocs.png` (2026-06-23) |
| Console errors | Not captured |
| Network errors | Not captured |
| JS exceptions | Not captured |
| Missing APIs | Not tracked |
| Layout/rendering bugs | 497 missing rects; 11 zero-area (4 with content); 209 offscreen |
| Input/event bugs | Not tested |
| Storage/cookie bugs | Not tested |
| Crash/hang | No crash |
| Likely root cause | CSS/layout gaps in flex/inline handling; partial script execution |
| Confirmed root cause | Not confirmed |
| Linked engine tasks | T4.3 |
| Tests added | None yet |
| Status | RESEARCHED |
| Evidence | Layout dump at `real_site_render_reactdocs.diag.txt` (2026-06-23) |

### news.ycombinator.com

| Field | Value |
|-------|-------|
| Site ID | hn-001 |
| URL | https://news.ycombinator.com |
| Category | News (9) |
| Login required | No |
| Expected behavior | Hacker News front page with story list |
| Current behavior | Mostly works — 777/816 elements (95%) get layout rects. 97 zero-area boxes but none with content. Best-performing real site tested. |
| Screenshot | `real_site_render_hackernews.png` (2026-06-23) |
| Console errors | Not captured |
| Network errors | Not captured |
| JS exceptions | Not captured |
| Missing APIs | Not tracked |
| Layout/rendering bugs | 31 missing rects; 97 zero-area boxes (0 with content — likely legitimate empty elements) |
| Input/event bugs | Not tested |
| Storage/cookie bugs | Not tested |
| Crash/hang | No crash |
| Likely root cause | Minor — mostly works. Table-based layout is well-supported. |
| Confirmed root cause | N/A (mostly works) |
| Linked engine tasks | None urgent |
| Tests added | None yet |
| Status | RESEARCHED |
| Evidence | Layout dump at `real_site_render_hackernews.diag.txt` (2026-06-23) |
