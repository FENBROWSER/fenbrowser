# FenBrowser — Known Gaps

> Auto-generated Gate 0 Reality Audit. Last refreshed: 2026-06-27.
> Gaps are classified by priority (0-5 per PLAN.MD).

## Priority 0 — Security-Critical

| Gap | Detail | Evidence |
|-----|--------|----------|
| No renderer sandbox | Renderer has direct filesystem/network access | Architecture: single process by default |
| No brokered network | Network requests originate from renderer process | NetworkClient runs in-process |
| IPC validation incomplete | Not all IPC messages have schema validation | IPC model partial |
| Origin isolation not enforced | SiteLock exists but not default | OopifPlanner present but not wired |

## Priority 1 — Real-Site Blockers

| Gap | Detail | Site impact | Evidence |
|-----|--------|-------------|----------|
| **Inline layout zero-height elements** | Many elements get layout rects with zero height despite having text content | GitHub: 102 elements with content but 0px height | GitHub diag dump |
| **Script loading order/scheduling** | External scripts may not execute in correct order, async/defer ordering unverified | x.com: only 10/81 elements get layout rects (scripts fail) | x.com diag dump |
| **Module script linking** | ModuleRecord graph resolution not implemented; dynamic import always rejects | Any site using ES modules | FenJS progress: "no host module resolver yet" |
| **Missing Web APIs not logged** | When JS accesses unimplemented API, no structured log or tracking | Unknown — no tracker exists | No missing_apis.json output |
| **Event loop / microtask timing** | Promise microtasks may run at wrong time relative to DOM operations | React/Vue/Svelte boot failures | No event loop trace output |
| **CSS flex/grid zero-height children** | Flex children with text content collapsing to zero height | GitHub sidebar nav: 200px-wide zero-height spans | GitHub diag dump |
| **External resource loading** | External CSS/JS/font bundles may not be fetched or applied | x.com: bundles from abs.twimg.com not loaded | x.com diagnostic |
| **XMLHttpRequest/Fetch in hosted engine** | 5 XHR tests fail in hosted browser engine context | Real-site XHR/fetch calls | Test failures |

## Priority 2 — Core Web Platform Gaps

| Gap | Detail | Evidence |
|-----|--------|----------|
| Form submission | Form element, form data, submit event — status unknown | No tests checked |
| iframe handling | Cross-origin iframe isolation, sandbox attributes | OopifPlanner present, wiring unknown |
| History API | pushState/replaceState/popstate | Not verified |
| WebSocket | Design intent only | CLAUDE.md: "WebSockets plan" |
| Service Worker | Design intent only | CLAUDE.md: "service worker plan" |
| Font loading API | FontFace, document.fonts | Not verified |
| Canvas 2D | Basic Skia-backed canvas probably works | Not verified |
| ResizeObserver | Stub or partial | CLAUDE.md: "ResizeObserver" listed |
| IntersectionObserver | Stub or partial | CLAUDE.md: "IntersectionObserver" listed |

## Priority 3 — Visible Interop Issues

| Gap | Detail | Evidence |
|-----|--------|----------|
| getComputedStyle gaps | May return wrong values for some properties | Not systematically tested |
| CSS custom properties | var() in shorthands, @property registration | Not verified |
| CSS container queries | Partial implementation | CascadeEngine references |
| CSS pseudo-elements | ::before/::after rendering; ::placeholder, ::selection | Not verified |
| Scroll behavior | smooth scrolling, scrollIntoView, scroll events | Acid2 fix involved scrollIntoView |
| Transforms | 3D transforms, transform-origin, perspective | Not verified |
| Animations | CSS animations, transitions, Web Animations API | Not verified |

## Priority 4 — Legacy/Rare

| Gap | Detail | Evidence |
|-----|--------|----------|
| `with` statement | 171/181 pass, 10 remaining edge cases | test262 results |
| Annex B oddities | Block-level function hoisting in sloppy mode | Most fixed, some edge cases remain |
| HTML parsing quirks | Fostering, misnested tags, formatting element reconstruction | Not fully tested against html5lib |

## Priority 5 — Non-Blocking Compliance

| Gap | Detail | Evidence |
|-----|--------|----------|
| RegExp `v` flag | Unicode sets mode | test262: property escape gaps |
| RegExp match indices | `hasIndices` (d flag) | Partial; some edge cases |
| Temporal non-ISO calendars | Chinese, Dangi, Islamic calendars partially stubbed | test262: 138 intl402/Temporal fails |
| Intl segmenter | Intl.Segmenter | test262: 27 fails |
| ES2025 proposals | import defer, source phase imports, Float16Array | test262: ~600 staging fails |
