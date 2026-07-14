# FenBrowser Architecture Risk Register

Snapshot date: 2026-07-14. Status values use the project taxonomy. A risk marked `BLOCKED_NEEDS_HUMAN_DECISION` names an architecture or security choice that agents must not make implicitly.

| ID | Area | Risk and failure mode | Evidence | Impact | Required mitigation | Status |
| --- | --- | --- | --- | --- | --- | --- |
| AR-001 | Process | The normal runtime is in-process; a page crash or compromise can affect the UI process | Factory default and current `debug-site` path | Critical | Decide the production default, then prove brokered Google navigation/input/frame/crash recovery | BLOCKED_NEEDS_HUMAN_DECISION |
| AR-002 | Network | Network broker code is present but the coordinator can fall back to direct `HttpClient`, and active resource callers are not wired to it | Reference search and `NetworkProcessCoordinator.SendAsync` | Critical | Decide fail-closed/fallback policy; route one end-to-end request with origin/cookie/CORS evidence | BLOCKED_NEEDS_HUMAN_DECISION |
| AR-003 | IPC | Renderer/network/target envelopes are separately hand-written and unversioned | IPC source audit | High | Define a versioned generated schema and migration rule before changing public IPC contracts | BLOCKED_NEEDS_HUMAN_DECISION |
| AR-004 | Memory | Strong host-object rows and reference-keyed caches can retain DOM graphs; weak/cycle semantics are not defined | `HostObjectTable`, `_hostHandleCache`, event listener stores | Critical | Approve the ownership/cycle model and add teardown/GC stress tests | BLOCKED_NEEDS_HUMAN_DECISION |
| AR-005 | JS heap | Browser mode disables automatic minor GC because roots can be lost | Runtime initialization comment and assignment | High | Find missing roots with a deterministic stress reduction before re-enabling collection | RESEARCHED |
| AR-006 | WebIDL | Generator presence can be mistaken for integrated bindings while generated code is excluded | FenEngine project file | High | Track generated coverage separately and integrate only after memory/exception/identity contracts are approved | RESEARCHED |
| AR-007 | Diagnostics | The summary can say no fatal error while the event-loop snapshot contains callback failures | Google bundle | High | Implement a timestamped cross-artifact first-blocker classifier and regression fixture | RESEARCHED |
| AR-008 | Diagnostics | Raw trace copy can race the asynchronous logger | Missing Google callback failure log records | High | Add an explicit log-drain/export boundary and test it | RESEARCHED |
| AR-009 | API tracking | Host expando reads become false missing-Web-API reports, causing agents to fix the wrong seam | Google missing API list | High | Add provenance/disposition and only promote confirmed interface members | RESEARCHED |
| AR-010 | Regression | Large test directories are compiled out, so green test runs can omit the changed subsystem | FenBrowser.Tests project file and discovery audit | High | Move focused contracts to included folders or deliberately revise project policy | RESEARCHED |
| AR-011 | Security | Image parsing remains in the renderer/UI trust domain | No decoder worker code path found | High | Design an isolated decoder protocol with limits and crash recovery after process contracts freeze | NOT_STARTED |
| AR-012 | Storage | There is no separate origin storage service or quota authority | Page-local `StorageService`; no process target | High | Define storage service ownership and IPC only after origin/process decisions | NOT_STARTED |
| AR-013 | Performance | Google's first captured frame takes 419.108 ms and triggers the watchdog | Google `style_layout.json` | Medium | Export allocation/GC/frame data before optimizing the measured stage | RESEARCHED |
| AR-014 | Artifact privacy | Trace bundles can contain URLs, headers, page text, scripts, cookies, and tokens | Bundle contents and raw logs | High | Add field-level redaction policy, retention policy, and secret-scanning tests | RESEARCHED |
| AR-015 | Architecture drift | Comments/docs disagree with active defaults and ownership (for example "defaults brokered" versus in-process behavior) | Factory summary comment versus implementation | Medium | Make compile/runtime truth authoritative and add contract tests for defaults | RESEARCHED |

## Review rule

Each mitigation must name a failing reproduction, an owning boundary, a test, and a generated artifact. Do not lower a risk status because code exists; lower it only when the stated failure mode is falsified.
