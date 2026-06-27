# FenBrowser — Diagnostics Specification

> Diagnostic spine design for Gate 1. Specifies trace format, log levels, categories, and output bundle structure.
> Implementation: T1.1 base trace sink implemented in `FenBrowser.Core/Logging/EngineLogSinks.cs`; T1.2-T1.8 remain open.

## Log Levels

| Level | Use |
|-------|-----|
| TRACE | Internal state transitions, per-operation details |
| DEBUG | Diagnostic information useful for debugging |
| INFO | Notable lifecycle events (navigation, parse, load, paint) |
| WARN | Non-fatal issues (missing API, slow operation, degraded mode) |
| ERROR | Fatal operation failures (script crash, network failure, parse error) |
| FATAL | Renderer crash, unrecoverable state |

## Trace Categories (25 Required)

```
Navigation, Network, HTMLParser, ResourceLoader, ScriptLoader,
JS, WebIDL, DOM, EventLoop, Microtask, Timer, CSSParser,
Selector, Cascade, Style, Layout, Paint, Compositor, Input,
Storage, Cookie, Security, IPC, Process, Crash, Performance
```

## Trace Event Schema

Every trace event (JSONL line) must include:

```json
{
  "ts": "2026-06-27T12:00:00.000Z",
  "level": "INFO",
  "category": "Navigation",
  "event": "NavigationStarted",
  "session_id": "s_abc123",
  "process_id": "p_main",
  "nav_id": "n_1",
  "doc_id": "d_1",
  "frame_id": "f_1",
  "realm_id": null,
  "script_id": null,
  "request_id": null,
  "task_id": null,
  "msg": "Navigation started to https://example.com",
  "data": { "url": "https://example.com", "disposition": "new" }
}
```

Current implementation note: `EngineLog` trace output now writes this diagnostic JSONL schema when `EnableTraceSink` is enabled. Event names can be supplied with the `fields["event"]` value, and exact trace category overrides can be supplied with `fields["traceCategory"]`. Later instrumentation tasks must populate navigation, document, frame, request, realm, script, and task IDs at their owning subsystem boundaries.

Required ID fields per event type:
- All events: `ts`, `level`, `category`, `event`, `session_id`, `process_id`
- Navigation events: + `nav_id`
- Document events: + `doc_id`, `nav_id`
- Frame events: + `frame_id`, `doc_id`
- Script events: + `script_id`, `doc_id`
- Network events: + `request_id`, `nav_id`
- JS execution: + `realm_id`, `script_id`
- Task/microtask: + `task_id`
- Layout/paint: + `doc_id`

## Script Loading Events

| Event | When |
|-------|------|
| `ScriptDiscovered` | Parser or document.write encounters `<script>` |
| `ScriptFetchStarted` | Network request for external script begins |
| `ScriptFetchCompleted` | Network response received |
| `ScriptReady` | Script fetched and ready to execute (respects async/defer ordering) |
| `ScriptExecutionStarted` | Script begins executing |
| `ScriptExecutionCompleted` | Script execution completes without error |
| `ScriptExecutionFailed` | Script throws unhandled exception |
| `DOMContentLoadedBlockedByScript` | Parser-blocking script delays DCL |
| `DOMContentLoadedFired` | DOMContentLoaded event dispatched |
| `LoadFired` | Window load event dispatched |

Per-script fields: `script_id`, `url`, `inline|external`, `classic|module`, `async`, `defer`, `parser_inserted`, `blocking_status`, `fetch_status`, `mime_type`, `execution_order`, `exception` if failed.

## Event Loop Events

| Event | When |
|-------|------|
| `TaskQueued` | Task added to a task queue |
| `TaskStarted` | Task begins execution |
| `TaskCompleted` | Task finishes execution |
| `MicrotaskQueued` | Microtask (Promise job) queued |
| `MicrotaskCheckpointStarted` | Microtask checkpoint begins |
| `MicrotaskExecuted` | Individual microtask executes |
| `MicrotaskCheckpointCompleted` | All pending microtasks executed |
| `TimerScheduled` | setTimeout/setInterval registered |
| `TimerFired` | Timer callback executes |
| `RequestAnimationFrameScheduled` | requestAnimationFrame callback registered |
| `RequestAnimationFrameFired` | rAF callbacks execute |
| `RenderOpportunityStarted` | Render opportunity (style/layout/paint) begins |
| `RenderOpportunityCompleted` | Render opportunity ends |

## Navigation Lifecycle Events

| Event | When |
|-------|------|
| `NavigationRequested` | URL bar or link click initiates navigation |
| `NavigationRedirected` | Server returns redirect |
| `NavigationResponseReceived` | HTTP response received |
| `NavigationCommitted` | Navigation commits to new document |
| `DocumentCreated` | Document object created |
| `HTMLParsingStarted` | HTML tokenization begins |
| `HTMLParsingCompleted` | HTML tree construction complete |
| `StylesheetDiscovered` | `<link rel=stylesheet>` or `@import` found |
| `StylesheetLoaded` | Stylesheet fetched and parsed |

## Diagnostic Bundle Format

Per-site trace bundle at `/traces/<site>/<run-id>/`:

| File | Content |
|------|---------|
| `summary.md` | Human-readable summary per PLAN.MD format |
| `trace.jsonl` | All structured trace events |
| `console.log` | Console API calls (console.log/warn/error) |
| `network.json` | Network request/response summary |
| `exceptions.json` | JS exceptions with stack traces |
| `missing_apis.json` | Missing API accesses (deduplicated) |
| `script_loading.json` | Per-script lifecycle records |
| `event_loop.json` | Task/microtask/timer/rAF trace |
| `style_layout.json` | Style/layout/paint events |
| `ipc.json` | IPC message events (when process isolation active) |
| `sandbox_denials.json` | Sandbox policy denials |
| `performance.json` | Timing, allocation, GC events |
| `dom_dump.html` | Serialized DOM tree |
| `style_dump.txt` | Computed style for every element |
| `layout_dump.txt` | Layout boxes with rects |
| `paint_dump.txt` | Paint commands |
| `display_list.txt` | Display list |
| `screenshot.png` | Rendered viewport |

## Summary.md Template

```markdown
# FenBrowser Site Diagnostic: <URL>

- **URL**: <url>
- **Run ID**: <run-id>
- **Timestamp**: <iso-timestamp>

## Visible Result
<description>

## Status Checklist
- [ ] Navigation started
- [ ] Navigation committed
- [ ] Document created
- [ ] HTML parsing started/completed
- [ ] Stylesheets discovered/loaded
- [ ] Scripts discovered/loaded/executed
- [ ] DOMContentLoaded fired
- [ ] Load event fired
- [ ] Style calculation run
- [ ] Layout run
- [ ] Paint run
- [ ] Pixels submitted

## Network
- Requests: N
- Failed: N
- Notable failures: <list>

## Scripts
- Discovered: N
- Executed: N
- Failed: N
- First fatal error: <error>

## Missing APIs
- Count: N
- Top: <list>

## Layout/Rendering
- Style calculation: <yes/no/count>
- Layout boxes: N
- Painted commands: N
- First paint blocker: <description>

## Root Cause
- **Likely**: <hypothesis>
- **Confirmed**: <yes/no>

## Next Task
<task-id or description>
```

## Debug Commands

```bash
fenbrowser --debug-site <url> --trace all --output <folder>
fenbrowser --dump-dom <url>
fenbrowser --dump-style <url>
fenbrowser --dump-layout <url>
fenbrowser --dump-paint <url>
fenbrowser --dump-display-list <url>
fenbrowser --screenshot <url>
fenbrowser --inspect-selector <url> "<selector>"
```

## Element Inspection Output

For `--inspect-selector`, each matched element shows:
```
Element: <div.example>
  id: my-id
  classes: example, active
  computed-display: block
  computed-position: static
  size: 200px × 100px
  margin: 10px 20px 10px 20px
  padding: 5px
  border: 1px solid #000
  layout-dirty-reason: child-added
  paint-status: painted
  visibility: visible
  --- Matched Rules ---
  .example { display: block; width: 200px; } [specificity: 0,1,0] ← winning
  div { color: red; } [specificity: 0,0,1]
  --- Why not visible (if hidden/zero-size/clipped) ---
  <reason>
```
