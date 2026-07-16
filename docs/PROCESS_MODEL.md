# FenBrowser Process Model

Status: RESEARCHED. Snapshot date: 2026-07-16.

## Current process audit

| Process role | Implementation | Active by default | Privileged operations observed | Status |
| --- | --- | --- | --- | --- |
| Browser/UI host | `FenBrowser.Host` | Yes | Window, input, child launch, filesystem-adjacent host work, direct HTTP wiring | INTEGRATED |
| Renderer per tab | `BrokeredProcessIsolationCoordinator`, renderer child host | No | Page DOM/JS/style/layout/paint and shared-memory frame production | IMPLEMENTED |
| Network | `NetworkProcessSession` and `NetworkProcessCoordinator` | No active request caller found | HTTP request execution | IMPLEMENTED |
| GPU | target session and compositor submission path | Brokered mode only | Raster/composite target work | INTEGRATED |
| Utility | target session | Brokered mode only | Bounded helper target | IMPLEMENTED |
| Storage | No separate process | No | Storage runs in browser/renderer surfaces | NOT_STARTED |
| Image decoder | No separate worker | No | Image decode runs in process | NOT_STARTED |
| Trace collector | No separate service | No | Logs are collected/copied by current process and Tooling | STUBBED |

The factory implementation, not its summary comment, is authoritative: an unset `FEN_PROCESS_ISOLATION` returns `InProcessIsolationCoordinator`. This discrepancy is a risk and must be fixed only after the default-mode decision is accepted.

## Current brokered startup evidence

- The compiled real-process acceptance is discovered but explicitly skipped under `BLOCK-PROC-002`.
- FenBrowser now resolves its own apphost when embedded by a test runner and preserves the child environment through Windows `CreateProcessW`; focused component tests pass.
- Strict AppContainer launch from the development checkout fails closed with Win32 error 2 because the renderer profile cannot traverse/read the runtime path.
- A temporary exact-profile-SID ACL experiment removed the immediate spawn error, but authenticated Ready/navigation/frame acceptance did not complete within 30 seconds. The ACLs were removed and their absence verified.
- Input, shared-memory frame delivery, controlled crash, UI survival, session-generation rejection, and orphan cleanup are therefore not accepted for the real brokered process path.

## Target responsibility contract

### Browser/UI process

Owns windows, tabs, profile/session state, permissions UI, child lifecycle, crash UI, DevTools host, WebDriver entry, and privileged capability issuance. It must not execute page DOM/CSS/JS in the final brokered topology.

### Renderer process per tab

Owns document lifecycle, parser, DOM, realm and JS runtime, WebIDL bindings, style, Box Tree, layout, Paint Tree/display list, event loop, input dispatch, and page-local storage frontends. It requests privileged work through validated asynchronous contracts.

### Network broker

Owns request execution, redirects, cookies, cache, CORS response filtering, MIME policy, TLS decisions, and request logs. The renderer supplies an origin-scoped request, never an unrestricted socket or client object.

### Storage service

Owns persistent origin storage, quota, schema/version recovery, and profile data. `sessionStorage` remains scoped to a browsing context but persistence and cross-document access must use a service contract once split.

### Image decoder worker

Accepts bounded encoded bytes plus format hints, returns a bounded decoded surface handle, and can be killed/restarted independently. It receives no filesystem or network capability.

### GPU/compositor service

Consumes validated display/raster commands or shared surfaces, schedules frames, tracks damage, and owns GPU/native handles. It cannot mutate DOM or security policy.

## Lifecycle contract

1. Host selects an explicit process policy and emits it into the session trace.
2. Host resolves the role-specific sandbox profile before launch.
3. Host creates a single-use capability token and launches the child.
4. Child validates startup arguments and returns an authenticated `Ready` message.
5. Host marks the child usable only after the handshake.
6. Requests have correlation IDs, deadlines, cancellation, payload budgets, and one terminal outcome.
7. Exit/hang invalidates outstanding handles and produces process, IPC, crash, and sandbox artifacts.
8. Restart creates a new process/session generation; stale tokens, frame handles, and requests are rejected.

## Crash and timeout behavior

| Failure | Required behavior | Current status |
| --- | --- | --- |
| Renderer crash | Preserve UI, mark tab crashed, capture last task/script/IPC, permit bounded restart | IMPLEMENTED |
| Renderer hang | Watchdog identifies last progress and offers termination/restart | STUBBED |
| Network crash | Fail outstanding requests explicitly; no silent privilege change | BLOCKED_NEEDS_HUMAN_DECISION |
| GPU crash | Drop invalid surfaces, preserve UI, request repaint after restart | DESIGNED |
| Utility crash | Fail only its bounded operation | DESIGNED |
| Storage crash | Fail transaction atomically and preserve durable data | NOT_STARTED |
| Decoder crash | Fail the image, preserve renderer/UI, record hostile input metadata | NOT_STARTED |

## Process acceptance matrix

- renderer: authenticated startup, navigation, input, frame, crash, hang, restart, stale-handle rejection;
- network: allowed request, policy denial, redirect, cancellation, oversized response, crash, no-fallback behavior;
- GPU: submit, malformed command, stale surface, crash/restart, resource release;
- utility: capability allow/deny, timeout, crash;
- storage and decoder: design and threat-model acceptance before implementation.

Default-mode, runtime ACL provisioning, fallback, and public process-contract decisions are `BLOCKED_NEEDS_HUMAN_DECISION` in `BLOCKERS.md`.
