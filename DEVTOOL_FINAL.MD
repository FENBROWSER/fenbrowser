# Fen DevTools vs Chrome DevTools vs Firefox DevTools

Date: March 28, 2026

## Executive Summary

Fen DevTools is not currently in the same class as Chrome DevTools or Firefox DevTools.

The current Fen implementation is a credible early inspector shell with:

- a native in-process DevTools UI
- a remote CDP-compatible transport surface
- usable Elements inspection and editing
- basic Console evaluation
- basic Network inspection with request/response body preview
- basic Sources viewing and script-source retrieval

That is enough to support early browser-engine bring-up and some day-to-day debugging. It is not enough to compete with the modern developer workflows supported by Chrome and Firefox.

My blunt assessment:

- Chrome DevTools: production-leading, broadest surface, deepest tooling
- Firefox DevTools: production-grade, slightly narrower than Chrome in some areas, still dramatically ahead of Fen
- Fen DevTools: early-stage foundation, roughly "basic inspector + partial protocol", not yet "full browser developer platform"

If Chrome is treated as 100 and Firefox as 85-95 depending on workflow, Fen is currently around 20-30 overall.

## Methodology

This analysis is based on:

1. Fen repo state in this workspace on March 28, 2026.
2. Local Fen sources and docs, especially:
   - `docs/VOLUME_V_DEVTOOLS.md`
   - `FenBrowser.DevTools/Panels/*.cs`
   - `FenBrowser.DevTools/Domains/*.cs`
   - `FenBrowser.DevTools/Core/*.cs`
   - `FenBrowser.Host/DevToolsHostAdapter.cs`
   - `FenBrowser.Host/ChromeManager.cs`
   - `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`
3. Official Chrome and Firefox DevTools documentation checked on March 28, 2026.

## Current Fen DevTools Surface

### What Fen has today

- Panels:
  - Elements
  - Console
  - Network
  - Sources
- Protocol/server:
  - remote debug server over WebSocket
  - `/json/version` and `/json/list` discovery endpoints
  - auth token support and explicit bind control
- DOM:
  - `DOM.getDocument`
  - child node requests
  - node highlighting
  - attribute editing
  - text-node editing
  - `DOM.getOuterHTML`
  - `DOM.setOuterHTML`
  - `DOM.querySelector`
  - `DOM.querySelectorAll`
- CSS:
  - computed style retrieval
  - matched-style retrieval
  - inline style editing through `CSS.setStyleTexts`
- Runtime:
  - `Runtime.enable`
  - `Runtime.evaluate`
  - `Runtime.getProperties`
  - remote-object handles for non-primitives
- Network:
  - request list
  - response headers
  - request/response body fetch
  - basic status, size, and duration display
- Debugger/Sources:
  - `Debugger.enable`
  - `Debugger.getScriptSource`
  - `Debugger.scriptParsed`
  - source list and source-text viewer

### What Fen clearly does not have yet

- no production debugger workflow
  - no user-facing breakpoints
  - no stepping UI
  - no watch expressions
  - no call stack panel
  - no scope panel
  - no conditional/logpoint UI
  - no event/XHR/DOM breakpoints in protocol/UI
- no performance tooling
  - no timeline
  - no frame profiler
  - no paint/layout/script tracing UI
  - no FPS/layer/rendering diagnostics panel
- no memory tooling
  - no heap snapshots
  - no allocation sampling
  - no leak tooling
  - no buffer/memory inspector
- no application/storage tooling
  - no IndexedDB panel
  - no cookie inspector panel
  - no local/session storage panel
  - no Cache Storage/service worker tooling UI
  - no manifest/PWA diagnostics
- no accessibility tooling
  - no accessibility tree
  - no ARIA/computed a11y inspection
  - no audit surface
- no device emulation or sensors
  - no responsive mode
  - no CPU/network emulation controls
  - no geolocation/orientation sensors panel
- no advanced network tooling
  - no waterfall breakdown
  - no initiator/source linkage UI
  - no HAR import/export
  - no request blocking or overrides UI
  - no websocket/SSE inspection
  - no cache disable/throttle controls
- no extensibility story
  - no extension/plugin API for third-party DevTools panels
- no mature UX layer
  - no search across all sources
  - no keyboard-rich workflows comparable to Chrome/Firefox
  - no persistent workspace/source-map-authoring workflow

## Comparison Matrix

| Area | Fen | Chrome | Firefox | Verdict |
|---|---|---|---|---|
| Core inspector | Basic and usable | Best-in-class | Production-grade | Fen is far behind |
| DOM editing | Good baseline | Deep | Deep | Fen has started well |
| CSS inspection | Basic computed and matched styles | Extensive | Extensive | Fen missing layout/debug overlays and richer rule tooling |
| Console | Basic REPL and object preview | Mature | Mature | Fen lacks workflow depth |
| Network | Basic list + headers + payload preview | Extensive | Extensive | Fen missing most serious network tooling |
| Sources/debugger | Source viewing only, minimal protocol | Extensive | Extensive | Largest current gap |
| Performance | Missing | Extensive | Extensive | Critical gap |
| Memory | Missing | Extensive | Strong | Critical gap |
| Storage/PWA | Missing | Extensive | Strong | Critical gap |
| Accessibility | Missing | Strong | Strong | Critical gap |
| Emulation/sensors | Missing | Strong | Strong | Critical gap |
| Remote debugging | Basic CDP bridge | Native, comprehensive | Native, comprehensive | Fen transport exists, protocol depth does not |
| Extensibility | Missing | Strong | Strong | Strategic gap |

## Detailed Comparison

### 1. Inspector / Elements

Fen status:

- Fen has real progress here.
- The Elements panel is the strongest part of the current stack.
- It supports DOM tree viewing, node highlighting, attribute edits, text edits, outerHTML editing, selector queries, computed styles, matched rules, and box-model visualization.

Chrome and Firefox:

- Chrome Elements and Firefox Inspector both go much further:
  - layout overlays for grid/flex/scroll/container diagnostics
  - event listener inspection
  - animation tooling
  - font tooling
  - transform/filter/shape tooling
  - source-linked CSS rule editing
  - richer badge/highlighter systems
  - more robust pseudo-state and state forcing

Assessment:

- Fen is not embarrassing here anymore.
- But it is still "basic inspector" while Chrome/Firefox are "full layout and styling workbenches."

### 2. Console / Runtime

Fen status:

- Console logs and JS evaluation work.
- Objects no longer collapse into a dead string because `Runtime.getProperties` now exists.
- The current console is still a thin REPL, not a mature debugging console.

Missing in Fen relative to Chrome/Firefox:

- richer console formatting
- expandable nested object trees
- tables/groups/traces/profile helpers
- watch/live expressions
- command-line utilities
- async stack context
- multi-context execution targeting
- preserved logs and richer filtering/search

Assessment:

- Fen console is usable for engine bring-up and quick poking.
- It is not yet a serious daily driver console.

### 3. Network

Fen status:

- Current Network panel shows requests and basic details.
- It can retrieve request post data and response bodies.
- It exposes basic status, size, MIME type, and timing.

Chrome and Firefox:

- Chrome Network supports filtering/sorting/search, blocking, throttling, screenshots, overrides, export, initiators, timing breakdowns, and much deeper request analysis.
- Firefox Network Monitor supports request details, performance analysis, throttling, web socket inspection, server-sent event inspection, and now network overrides.

Assessment:

- Fen has a baseline viewer.
- Chrome and Firefox have full network labs.
- This is one of the biggest practical workflow gaps.

### 4. Sources / JavaScript Debugger

Fen status:

- Fen now has a Sources panel and can retrieve script text.
- `Debugger.scriptParsed` and `Debugger.getScriptSource` exist.
- There are internal engine-side breakpoint and stepping primitives in `FenBrowser.FenEngine.DevTools.DevToolsCore`, but they are not exposed as a complete protocol/UI workflow.

Chrome and Firefox:

- Chrome Sources and Firefox Debugger support:
  - breakpoints
  - conditional breakpoints
  - logpoints
  - stepping
  - watch expressions
  - scopes
  - call stacks
  - source maps
  - pretty print
  - worker debugging
  - remote target debugging
  - mutation/event/XHR breakpoints and related pause controls

Assessment:

- Fen has crossed from "no source visibility" to "basic source visibility."
- It has not crossed into "real debugger" territory.
- This is the single most important gap if the goal is parity with Chrome/Firefox.

### 5. CSS and Layout Tooling

Fen status:

- Computed styles and matched styles are available.
- Inline style editing exists.
- Box-model visualization exists.

Missing in Fen:

- grid inspector
- flexbox inspector
- visual layout overlays
- font editor
- color picker/workflow
- transform/filter/shape editors
- event listener inspection from the inspector
- richer source origin tracing and stylesheet navigation

Chrome and Firefox:

- Both browsers provide much deeper CSS and layout workflows.
- Firefox in particular has a mature Inspector surface for grids, flexbox, animations, fonts, transforms, filters, and shapes.

Assessment:

- Fen is at "inspect/edit basic CSS."
- Chrome/Firefox are at "debug layout systems visually and interactively."

### 6. Performance

Fen status:

- No true DevTools performance panel is present.
- No trace viewer, no frame timeline, no runtime performance analysis UI.

Chrome and Firefox:

- Chrome has a mature Performance panel and related rendering diagnostics.
- Firefox has a dedicated Performance panel for responsiveness, JavaScript, and layout performance.

Assessment:

- This is a hard blocker for "on par" claims.
- Without performance tooling, Fen is not competing with browser-grade DevTools.

### 7. Memory

Fen status:

- No memory panel.
- No heap/object inspection workflow for leaks or allocation patterns.

Chrome and Firefox:

- Chrome has heap snapshots, allocation instrumentation, allocation sampling, and related memory debugging tools.
- Firefox exposes memory tooling as part of its DevTools set.

Assessment:

- Another hard blocker for parity.

### 8. Application / Storage / PWA / Workers

Fen status:

- No first-class storage/application UI.
- No service worker inspection panel.
- No manifest/PWA diagnostics.
- No storage tree for cookies, Cache Storage, IndexedDB, local storage, or session storage.

Chrome and Firefox:

- Chrome Application panel covers manifest, service workers, storage, cache data, and related application state.
- Firefox provides an Application panel and a Storage Inspector.

Assessment:

- Fen is substantially behind in modern web-app debugging workflows.

### 9. Accessibility

Fen status:

- No accessibility inspector is present.

Chrome and Firefox:

- Chrome exposes accessibility-tree and computed accessibility tooling in DevTools.
- Firefox has a dedicated Accessibility Inspector with checks and simulation workflows.

Assessment:

- Fen currently has no meaningful story here.

### 10. Device Emulation / Sensors / Responsive Workflows

Fen status:

- No responsive design mode.
- No device toolbar.
- No CPU/network/geolocation/orientation simulation UI.

Chrome and Firefox:

- Chrome Device Mode supports viewport simulation, CPU/network throttling, and sensors.
- Firefox has Responsive Design Mode and remote/mobile-oriented workflows.

Assessment:

- Fen currently cannot support a major class of modern front-end debugging workflows.

### 11. Remote Debugging, Multi-Target, and Platform Reach

Fen status:

- Fen has a remote debug server and CDP-style transport.
- That is a strong architectural decision.
- However, the protocol depth behind the transport is still narrow.

Chrome and Firefox:

- Both browsers support mature remote and multi-target debugging workflows.
- Firefox explicitly supports targeting pages, workers, browser internals, add-ons, and remote devices.

Assessment:

- Fen has the right outer shell here, but not the protocol/domain completeness to exploit it.

### 12. Extensibility and Ecosystem

Fen status:

- No DevTools extension or plugin model is visible in the current implementation.

Chrome and Firefox:

- Chrome supports DevTools extension APIs and panel integration.
- Firefox also supports extending DevTools via WebExtensions-based DevTools APIs.

Assessment:

- This is a strategic gap, not just a missing feature.
- Without extensibility, Fen must build every advanced tool itself.

## Where Fen Is Actually Strong

This comparison should not ignore the real positives:

1. Native in-process DevTools UI is a legitimate differentiator.
   - Chrome/Firefox implement DevTools with web tech inside the browser.
   - Fen's native Skia-based tools can be a strength if they stay fast, stable, and tightly integrated with engine state.

2. The architecture is not hopeless.
   - Fen already has:
     - a panel framework
     - a host abstraction
     - a protocol router
     - a remote debug server
     - engine-side debugger/source registration primitives

3. Elements-first progress is the right first move.
   - Browser bring-up usually benefits most from DOM/CSS/console/network basics before deeper trace tooling.

4. CDP-style compatibility gives Fen leverage.
   - If Fen fills out the right domains, external tooling and automation can become much more useful.

## What Prevents Parity Today

These are the real blockers, ordered by severity:

### Tier 1: Absolute blockers

- no real JavaScript debugger workflow
- no performance panel
- no memory tooling
- no storage/application tooling
- no accessibility tooling

### Tier 2: Daily workflow blockers

- weak network tooling
- shallow console UX
- no responsive/device/sensors tooling
- limited CSS/layout diagnostics

### Tier 3: Platform maturity blockers

- no extensibility model
- no ecosystem hooks
- no advanced multi-target workflow
- no polished search/filter/navigation model across tools

## What "On Par" Would Actually Require

To be honestly comparable to Chrome/Firefox, Fen needs at least:

### Minimum competitive baseline

- breakpoints, stepping, call stacks, scopes, watches, pretty print
- real Network timing/initiator/filter/search tooling
- Performance trace/timeline panel
- Storage/Application panel
- Accessibility panel
- responsive/device emulation
- richer CSS/layout overlays

### Strong parity baseline

- source maps
- worker/service worker debugging
- request blocking and response overrides
- screenshot/filmstrip or equivalent loading visualization
- heap snapshots and allocation profiling
- trace save/load/export
- robust keyboard-centric UX

### Beyond parity

- unique native-engine diagnostics not available in Chrome/Firefox
  - Box Tree inspector
  - Compositor diagnostics
  - Renderer invalidation visualizer
  - Strut/text baseline diagnostics
  - layout cost heatmaps
  - DOM-to-Box-to-paint correlation views

That last category is where Fen can eventually stop chasing and start differentiating.

## Recommended Roadmap

### Phase 1: Make Fen a real debugger

- expose breakpoints through protocol and UI
- add pause/resume/step over/step into/step out
- add call stack, scope, and watch panes
- add source search and source maps

### Phase 2: Make Fen usable for front-end debugging

- build responsive/device mode
- add network throttling and request blocking
- add better CSS/layout overlays for grid/flex/box diagnostics
- add console tree expansion and richer formatting

### Phase 3: Make Fen usable for serious performance work

- add performance tracing and timeline UI
- add rendering/frame/FPS diagnostics
- add memory inspection and heap workflows

### Phase 4: Make Fen usable for modern web-app workflows

- add Application panel
- add Storage inspector
- add service worker tooling
- add manifest/PWA diagnostics
- add accessibility inspection

### Phase 5: Make Fen differentiated

- add Fen-specific engine diagnostics
- add renderer/compositor/pipeline introspection
- add protocol surfaces that expose browser-engine internals safely

## Final Verdict

Fen DevTools is no longer a toy, but it is still far from Chrome DevTools and Firefox DevTools.

Today it is best described as:

- a promising native DevTools foundation
- with a good early Elements focus
- with partial runtime/network/source plumbing
- but without the major subsystems that define a modern browser debugging platform

The correct strategic posture is not to pretend Fen is close to parity. It is not.

The correct posture is:

1. Finish the debugger.
2. Add performance and storage/application tooling.
3. Add accessibility and responsive tooling.
4. Then build Fen-native diagnostics that Chrome and Firefox do not have.

That path is realistic. "Complete parity now" is not.

## Sources

### Fen repo evidence

- `docs/VOLUME_V_DEVTOOLS.md`
- `FenBrowser.DevTools/Panels/ElementsPanel.cs`
- `FenBrowser.DevTools/Panels/ConsolePanel.cs`
- `FenBrowser.DevTools/Panels/NetworkPanel.cs`
- `FenBrowser.DevTools/Panels/SourcesPanel.cs`
- `FenBrowser.DevTools/Domains/DomDomain.cs`
- `FenBrowser.DevTools/Domains/CSSDomain.cs`
- `FenBrowser.DevTools/Domains/RuntimeDomain.cs`
- `FenBrowser.DevTools/Domains/NetworkDomain.cs`
- `FenBrowser.DevTools/Domains/DebuggerDomain.cs`
- `FenBrowser.DevTools/Core/RemoteDebugServer.cs`
- `FenBrowser.Host/DevToolsHostAdapter.cs`
- `FenBrowser.Host/ChromeManager.cs`
- `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`

### Official Chrome DevTools docs checked March 28, 2026

- Chrome DevTools overview:
  - https://developer.chrome.com/docs/devtools/overview
- Network panel overview:
  - https://developer.chrome.com/docs/devtools/network/overview
- Network tutorial/reference:
  - https://developer.chrome.com/docs/devtools/network
  - https://developer.chrome.com/docs/devtools/network/reference/
- Performance:
  - https://developer.chrome.com/docs/devtools/performance
  - https://developer.chrome.com/docs/devtools/performance/reference
- Memory:
  - https://developer.chrome.com/docs/devtools/memory/
- Application panel:
  - https://developer.chrome.com/docs/devtools/application
- Accessibility:
  - https://developer.chrome.com/docs/devtools/accessibility/reference
- Device mode:
  - https://developer.chrome.com/docs/devtools/device-mode/
- CSS Grid inspection example:
  - https://developer.chrome.com/docs/devtools/css/grid
- DevTools extensibility:
  - https://developer.chrome.com/docs/extensions/reference/api/devtools/panels
  - https://developer.chrome.com/docs/extensions/mv2/devtools

### Official Firefox DevTools docs checked March 28, 2026

- Firefox DevTools user docs:
  - https://firefox-source-docs.mozilla.org/devtools-user/
- Page Inspector:
  - https://firefox-source-docs.mozilla.org/devtools-user/page_inspector/
- JavaScript Debugger:
  - https://firefox-source-docs.mozilla.org/devtools-user/debugger/
- Network Monitor:
  - https://firefox-source-docs.mozilla.org/devtools-user/network_monitor/
- Storage Inspector:
  - https://firefox-source-docs.mozilla.org/devtools-user/storage_inspector/index.html
- Accessibility Inspector:
  - https://firefox-source-docs.mozilla.org/devtools-user/accessibility_inspector/index.html
- CSS Grid Inspector:
  - https://firefox-source-docs.mozilla.org/devtools-user/page_inspector/how_to/examine_grid_layouts/
- DevTools extension APIs:
  - https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/devtools
  - https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Extending_the_developer_tools
