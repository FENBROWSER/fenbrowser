# FenBrowser Network, Fetch, Cookie, and Storage Tracker

Status: INTEGRATED for current in-process real-site loading; broker enforcement is STUBBED. Snapshot date: 2026-07-14.

## Current evidence

The current Google bundle recorded 27 requests and rendered the main page. Its single reported failure is a `data:` image URI, which indicates capture classification needs correction rather than a confirmed HTTP failure. The audit found direct `HttpClient` paths in Core/Host and no caller of `NetworkProcessCoordinator.SendAsync` in the active engine/resource path.

## Capability view

| Area | Status | Current truth | Next proof |
| --- | --- | --- | --- |
| URL/HTTP/TLS and redirects | INTEGRATED | Real pages load through current resource path | Redirect/error/MIME trace reductions |
| Resource loader visibility | INTEGRATED | Bundle contains request records | Stable request IDs across discovery/fetch/script/style |
| Fetch API | INTEGRATED | Manual browser API path exists | Selected local WPT for body/headers/abort/error behavior |
| XHR | IMPLEMENTED | Source exists, with some project exclusions | Compile-path audit and local WPT |
| CORS | INTEGRATED | Policy surfaces exist | Negative/positive selected WPT and policy decision trace |
| Cookies | INTEGRATED | Cookie surfaces exist | SameSite/domain/path/redirect real tests and redacted trace |
| Cache | IMPLEMENTED | Cache surfaces exist | Revalidation/vary/partition behavior tests |
| `localStorage` / `sessionStorage` | INTEGRATED | Page APIs exist | Origin/navigation/quota/error reductions |
| IndexedDB | STUBBED | No current real-site acceptance evidence | Architecture, quota, transactions, WPT plan |
| WebSocket | IMPLEMENTED | FenWebSocket host exists | Network policy, lifecycle, close/error tests |
| Service worker | STUBBED | No real-site acceptance evidence | Separate design after core fetch/event-loop stability |
| Network broker | IMPLEMENTED | Child/coordinator/token/limits exist | Wire active resource path and remove implicit bypass |

## Required request record

Every request requires request/navigation/frame IDs, initiator and origin, URL with redaction, method, destination, mode, credentials, redirect chain, cache disposition, cookie disposition, CORS/CSP/mixed-content decision, MIME, status, byte counts, timing, cancellation/timeout, process path, and terminal failure code.

## Broker migration gate

Changing the active request path or its fallback behavior is `BLOCKED_NEEDS_HUMAN_DECISION`. Before a switch:

1. document failure behavior when the child is unavailable;
2. run current and broker paths in lockstep for selected fixtures;
3. compare status, headers, redirect chain, exposed body, cookies, CORS result, and errors;
4. enforce cancellation, response-size ceilings, timeouts, crash recovery, and trace coverage;
5. prove the renderer has no alternate ambient request path.
