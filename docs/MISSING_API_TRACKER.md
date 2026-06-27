# FenBrowser — Missing API Tracker

> Logged when JavaScript accesses an unimplemented browser API.
> Format per PLAN.MD § "Missing API Tracker".
> **Currently empty** — tracking infrastructure (T1.6) must be built first.

## Tracker Format

| Field | Description |
|-------|-------------|
| API name | Full property/method path, e.g. `window.ResizeObserver`, `Element.prototype.attachShadow` |
| Object/prototype | The object on which the API is expected |
| Site | URL where first seen |
| Script URL | Source script URL (if available) |
| Line/column | Source position (if available) |
| First seen | Trace ID or timestamp |
| Exception text | The error thrown (usually TypeError: X is not a function / undefined is not an object) |
| Priority | 0-5 per PLAN.MD priority scale |
| Linked WPT tests | WPT test paths covering this API |
| Owner | Agent or subsystem owner |
| Implementation status | NOT_STARTED / STUBBED / PARTIAL / IMPLEMENTED / TESTED |
| Workaround/stub | Description of any stub behavior, marked STUBBED not DONE |

## Priority Assignment

- **0**: Security-critical APIs (CSP, CORS, sandbox)
- **1**: Real-site blockers — APIs whose absence causes blank pages or fatal crashes
- **2**: Core modern web platform APIs needed by common frameworks
- **3**: Visible interop issues affecting layout, input, or rendering
- **4**: Legacy/rare APIs
- **5**: Non-blocking obscure APIs

## Entries

*(None yet — T1.6 diagnostic spine must be built first)*

## Implementation Notes

The tracker infrastructure should:
1. Hook into `IHostHooks` / `BrowserFenJsHostHooks` — any time JS accesses a property that doesn't exist on the host object table, log it
2. Deduplicate by API name (first-seen trace ID preserved)
3. Track per-site and globally
4. Output `missing_apis.json` as part of the trace bundle
5. Never silently return fake values unless documented with STUBBED status
