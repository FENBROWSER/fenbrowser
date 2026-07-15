# FenBrowser Real-Site Debugging

Status: DESIGNED from the current `debug-site` workflow and 2026-07-14 Google evidence.

## Failure buckets

| Bucket | Classification | Owning first inspection |
| --- | --- | --- |
| A | Navigation | URL, redirects, commit, document/frame creation, history/location |
| B | Network | HTTP/TLS, fetch/XHR, CORS, cookies, cache, redirects, MIME |
| C | Script loading | parser blocking, async/defer, modules, dynamic import, order |
| D | JavaScript language | parser/compiler/VM/builtins/promises/modules after host causes are excluded |
| E | WebIDL/bindings | conversion, overload, brand, descriptors, wrapper identity, exception mapping |
| F | DOM API | missing/wrong Node, Element, Document, events, observers, custom elements, shadow DOM |
| G | Event loop | tasks, microtasks, timers, rAF, lifecycle timing, render opportunities |
| H | CSS/style | selectors, cascade, computed values, CSSOM, invalidation |
| I | Layout | Box Tree, formatting contexts, geometry, measurement, scrolling |
| J | Paint/compositing | Paint Tree, order, clip, raster, frame scheduling, submission |
| K | Storage/security | storage, cookies, origin policy, CSP, sandbox, permissions |
| L | Shell/process | host integration, input, renderer lifecycle, IPC, crash/hang |

## Triage workflow

1. Record the exact URL and visible symptom.
2. Stop only FenBrowser processes that belong to the intended clean repro; do not disrupt unrelated tools.
3. Clear only the relevant workspace `logs/` artifacts.
4. Build the smallest executable surface in an unlocked configuration.
5. Run `debug-site` with an explicit settle value. For interaction acceptance, run `debug-site-interact <url> <target_selector> <text> <submit_selector> <settle_ms> <interaction_settle_ms>` after the passive bundle is stable. The interaction runner requires a bounded quiet window on one terminal navigation generation so a delayed redirect is not mistaken for the final result. The diagnostic host and screenshots use the same fixed 1280x800 viewport; do not compare geometry from a differently sized host.
6. Inspect, in order: screenshot, summary/lifecycle, script loading, event loop, exceptions, missing APIs, network, DOM, style/layout/paint, raw trace/log.
7. Identify the earliest candidate that blocks the next missing milestone.
8. Confirm causality with a second run or a minimized local fixture.
9. Fix the earliest owning stage only.
10. Add a local regression or selected WPT reduction, rerun it, then rerun the exact site and compare artifacts.

If the main UI renders, do not reopen navigation/bootstrap theories without evidence. Move to input, measurement, rendering fidelity, or the first remaining recorded failure.

## Failure evidence rules

- A console warning is not automatically fatal.
- A missing property read is not automatically a missing Web API.
- A failed optional image or analytics request is not automatically a network blocker.
- A callback failure is a candidate only when its error and affected milestone are preserved.
- A layout zero-area count needs element geometry/content evidence.
- Passing unit tests do not prove the site result; a fresh bundle and screenshot are required.
- A terminal URL is not sufficient proof that the active document was published. The final lifecycle generation, DOM/root text, style/layout dumps, and screenshot must describe the same document; disagreement is a navigation/render-state defect.
- `debug-site` owns `logs/debug_site_screenshot.png`; the live renderer owns `logs/debug_screenshot.png`. Do not substitute one artifact for the other during bundle export.

## Real-site triage record

Every run added to `REAL_SITE_TRACKER.md` must contain:

```text
Site:
URL:
Current visible result:
Expected visible result:
First fatal console error:
First fatal network error:
First missing API:
First layout blocker:
Script loading status:
DOMContentLoaded fired:
Load fired:
Main framework detected:
Likely failure bucket:
Confirmed failure bucket:
Minimal reproduction:
Engine subsystem owner:
Fix task:
Regression test:
Evidence:
```

## Reduction workflow

Choose the durable regression surface in this order:

1. Existing selected WPT from `C:/Users/udayk/Videos/wpt` when it directly covers the behavior.
2. A minimized HTML/CSS/JS fixture under an included test project fixture directory.
3. A focused C# unit/integration test that drives the active production path.
4. A visual screenshot comparison when pixels are the failure.

Do not place fixtures or scripts at repository root. Runtime outputs remain in `logs/`; generated reports remain in `Results/`; reusable scripts remain in `scripts/`.

## Regression record

```text
Regression ID:
Detected By:
Affected Area:
Previous Behavior:
New Behavior:
Reproduction Steps:
Failing Test:
Suspected Cause:
Owner Agent:
Fix Status:
Regression Test Added:
Evidence:
```

The fix is not `DONE` until the original reproduction, focused regression, tracker, and fresh after-bundle all agree.
