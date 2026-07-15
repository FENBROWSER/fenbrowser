# FenBrowser Diagnostic Spine

Status: INTEGRATED with documented Gate 1 gaps. Snapshot date: 2026-07-14; bundle contract revalidated 2026-07-15.

Runtime artifacts follow the repository path policy and live under `logs/`, not at repository root or in `docs/`. The per-site bundle path is:

`logs/real-site/<site>/<run-id>/`

## Current entrypoint

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- debug-site <url> <settle_ms>
```

The current CLI accepts a URL and optional settle time. The proposed `--trace`, `--output`, standalone dump commands, and selector inspection are NOT_STARTED and must not be documented as working commands.

## Current versus required capability

| Capability | Status | Current truth | Required closure |
| --- | --- | --- | --- |
| Structured NDJSON | INTEGRATED | `EngineLog` writes `logs.ndjson` and Tooling flushes accepted records before bundle copy | Add export-failure isolation coverage |
| Diagnostic trace JSONL | INTEGRATED | Core fields and category mapping exist | Populate stable session/document/frame/realm/request/task IDs at owners |
| Navigation lifecycle | TESTED | Google has six ordered terminal transitions | Add redirect/failure/frame reductions |
| Script loading snapshot | TESTED | Per-script identity, source coordinates, fetch, batch, and execution state | Normalize dynamic-script discovery/execution populations |
| Event-loop snapshot | TESTED | DCL/load, timers, rAF counters, and bounded typed timer/event/unhandled-Promise failures with task/source/receiver/stack provenance | Add microtask, rAF, repeating-timer, and invalidation reductions |
| Network capture | INTEGRATED | Request/response records and counts | Correct non-HTTP schemes and add policy/cookie/CORS disposition |
| Missing API runtime tracker | TESTED | Rich per-site records are written under `logs/missing_apis/` | Export that schema into the run bundle and classify probes/expandos |
| First blocker summary | TESTED | Typed `first_blocker.json` models 19 milestones and separates non-fatal/unverified work | Normalize authoritative lifecycle detail and add missing-artifact fixtures |
| Exceptions artifact | TESTED | Timer, event-listener, and unhandled-Promise totals and retained typed records share the drained event-loop snapshot | Add microtask, parser, IPC, crash, redaction, and export-failure sources |
| DOM/style/layout/paint dumps | INTEGRATED | Current bundle has text dumps and screenshot | Add HTML DOM serialization contract and selector inspection |
| IPC/sandbox/performance artifacts | NOT_STARTED | Data sources exist outside the bundle | Emit typed files even when inactive, with an explicit inactive reason |

## Trace event envelope

Target schema: `fenbrowser.trace.v1`. Every event must contain the following keys; unknown identifiers are `null`, never omitted.

```json
{
  "schema_version": 1,
  "timestamp": "2026-07-14T07:59:06.7760907Z",
  "sequence": 2042,
  "level": "WARN",
  "category": "EventLoop",
  "event_name": "TaskFailed",
  "browser_session_id": "session-...",
  "process_id": 26572,
  "process_role": "renderer",
  "navigation_id": "1",
  "frame_id": "main",
  "document_id": "document-1",
  "realm_id": "realm-1",
  "script_id": "script-10",
  "request_id": null,
  "task_id": "timer-12",
  "message": "event-loop callback task failed",
  "data": {
    "error_type": "TypeError",
    "error": "..."
  }
}
```

The current JSONL keys use short names such as `ts`, `event`, and `nav_id`. Migration must be additive or versioned; it must not silently break existing bundle readers.

## Levels and categories

Required levels: `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`, `FATAL`.

Required categories:

`Navigation`, `Network`, `HTMLParser`, `ResourceLoader`, `ScriptLoader`, `JS`, `WebIDL`, `DOM`, `EventLoop`, `Microtask`, `Timer`, `CSSParser`, `Selector`, `Cascade`, `Style`, `Layout`, `Paint`, `Compositor`, `Input`, `Storage`, `Cookie`, `Security`, `IPC`, `Process`, `Crash`, `Performance`.

Current `MissingApiTracker` emits category `MissingAPI`, which is outside this contract. A missing interface member belongs to `WebIDL` or `DOM`; the event name remains `MissingApiObserved`.

## Required trace points

| Stage | Required event points | Required stage data |
| --- | --- | --- |
| Navigation | `NavigationStarted`, `NavigationRedirected`, `NavigationResponseReceived`, `NavigationCommitted`, `NavigationFailed` | URL, initiator, redirect chain, status, MIME, commit source |
| Document/parser | `DocumentCreated`, `HTMLParsingStarted`, `HTMLParsingCompleted` | document/frame IDs, bytes/tokens/nodes, parse mode, error count |
| Resources | `StylesheetDiscovered`, `StylesheetFetchStarted`, `StylesheetLoaded`, `ResourceFailed` | request ID, URL, initiator, type, blocking state, MIME |
| Scripts | `ScriptDiscovered`, `ScriptFetchStarted`, `ScriptFetchCompleted`, `ScriptReady`, `ScriptExecutionStarted`, `ScriptExecutionCompleted`, `ScriptExecutionFailed`, `DOMContentLoadedBlockedByScript` | stable script ID, source URL/coordinates, classic/module, async/defer/parser-inserted, execution order |
| JS | `JsExceptionThrown`, `UnhandledPromiseRejection`, `PromiseRejectionHandled` | realm/script/source/stack, handled state, fatality |
| WebIDL/DOM | `BindingConversionFailed`, `BrandCheckFailed`, `MissingApiObserved`, `DomExceptionThrown`, `MutationObserverDelivered`, `CustomElementReactionFailed` | interface/member, receiver brand, argument index, exception, source provenance |
| Event loop | `TaskQueued`, `TaskStarted`, `TaskCompleted`, `TaskFailed`, checkpoint start/execute/complete, timer schedule/fire, rAF schedule/fire, render opportunity start/complete | task source, queue, parent task, callback ID, timestamps, exception |
| Network/security | request/response/redirect/CORS/cookie/cache/CSP/mixed-content decisions | origin, credentials, policy decision, response exposure, redacted headers |
| Style/layout/paint | calculation start/complete, dirty reason, Box Tree build, layout start/complete, Paint Tree build, raster/submit | counts, durations, viewport, invalidation cause, blocker |
| Input | hit test, focus change, dispatch start/complete, default action, text edit, navigation activation | coordinates, target, event phase, prevented state, resulting value/navigation |
| Process/IPC | child launch/ready/exit, envelope send/receive/reject/timeout, sandbox allow/deny | role, sender/receiver, schema version, type, correlation, byte count, permission, rejection |
| Performance/crash | long task, allocation/GC, frame budget, hang watchdog, crash | duration, allocation, GC generation/pause, last task/IPC/script, dump path |

## First fatal blocker algorithm

The classifier is TESTED for the current typed milestone model. Further artifact families must join the same algorithm rather than introduce independent first-error strings:

1. Load lifecycle, network, script, event-loop, exception, missing-API, style/layout, IPC, sandbox, crash, and raw trace records.
2. Normalize every candidate to one timestamp/sequence domain and attach its blocked milestone.
3. Discard non-fatal feature probes, site expandos, optional subresource failures, and failures after the acceptance milestone unless they explain an observed symptom.
4. Sort remaining candidates by causal timestamp, then select the earliest candidate that prevents the next required milestone.
5. Map it to failure bucket A-L and an owning subsystem.
6. Emit `first_blocker.json` with candidate evidence and render the same record into `summary.md`/`summary.json`.
7. If no fatal blocker exists, say `none`; list the first remaining acceptance gap separately.

A missing property is not fatal merely because it was read. It becomes a fatal candidate only when a standards-defined member is confirmed and the access is causally linked to an exception, failed callback, missing milestone, or visible failure.

## Bundle contract

| Artifact | Status | Contract |
| --- | --- | --- |
| `summary.md`, `summary.json` | INTEGRATED | Human and machine run summary |
| `trace.jsonl`, `logs.ndjson` | INTEGRATED | Structured event streams, flushed through the run boundary |
| `console.log`, `exceptions.json` | INTEGRATED | Console plus typed callback exception records with agreeing totals |
| `network.json` | INTEGRATED | Requests, responses, failures, policy disposition |
| `missing_apis.json` | STUBBED | Rich classified records with provenance |
| `script_loading.json` | INTEGRATED | Per-script lifecycle |
| `event_loop.json` | INTEGRATED | Tasks, microtasks, timers, rAF, lifecycle |
| `style_layout.json` | INTEGRATED | Style/layout/paint/raster summary |
| `ipc.json` | NOT_STARTED | IPC metadata; empty typed record with inactive reason in in-process mode |
| `sandbox_denials.json` | NOT_STARTED | Policy denials; empty typed record with inactive reason if no sandbox |
| `performance.json` | NOT_STARTED | Navigation/stage/frame/allocation/GC/long-task metrics |
| `first_blocker.json` | TESTED | Deterministic typed result, evidence candidates, contradictions, non-fatal defects, and 19 milestone states |
| `dom_dump.html` | NOT_STARTED | Current bundle emits `dom_dump.txt`; canonical HTML serialization remains to be added |
| `style_dump.txt` | INTEGRATED | DOM-preorder computed-style snapshot |
| `layout_dump.txt` | INTEGRATED | Box/LayoutBox geometry snapshot |
| `paint_dump.txt` | INTEGRATED | Paint Tree snapshot |
| `display_list.txt` | STUBBED | Current file is a flattened Paint Tree proxy, not a canonical display-list command stream |
| `screenshot.png` | INTEGRATED | 1280x800 current tooling viewport |
| `artifact_manifest.json` | INTEGRATED | Must list the full required contract, not only files already implemented |

## Security and privacy

Trace output may contain sensitive URLs, query strings, headers, cookies, local file paths, page text, or tokens. Before bundles leave the local workspace:

- redact `Authorization`, `Cookie`, `Set-Cookie`, bearer tokens, passwords, and configured query parameters;
- cap message, stack, body, and payload sizes;
- record that redaction occurred without recording the secret;
- keep raw page content and screenshots local by default;
- never let diagnostic failures alter page behavior.

## Gate 1 implementation order

1. Preserve and classify event-loop/script/promise exceptions; add log drain.
2. Implement deterministic first blocker output.
3. Unify and classify missing API records.
4. Export `ipc.json`.
5. Export `sandbox_denials.json`.
6. Export `performance.json`.
7. Add standalone dump and selector-inspection commands.
8. Move regression tests onto included test surfaces and verify with a local failure fixture plus a fresh Google run.
