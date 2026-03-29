# FenBrowser Codex - Volume V: Developer Tools

**State as of:** 2026-03-28
**Codex Version:** 1.0

## 1. Overview

`FenBrowser.DevTools` provides the inspection and debugging capabilities for the browser. It features a unique **Dual-Mode Architecture**:

1.  **In-Process UI**: A native, high-performance overlay rendered directly by the Skia engine.
2.  **Remote Protocol**: A fully compliant implementation of the Chrome DevTools Protocol (CDP), allowing external tools (VS Code, Chrome) to debug FenBrowser.

## 2. In-Process UI (`Panels/`, `Core/`)

Unlike most browsers that implement DevTools as a web page (HTML/JS), FenBrowser's internal DevTools are **native C# components** rendered via SkiaSharp. This ensures they remain responsive even if the page JS thread hangs.

### 2.1 DevToolsController

The central hub managing the UI.

- **Docking**: Supports Bottom, Right, and Undocked modes.
- **Input Handling**: Intercepts mouse/keyboard events before they reach the page (when active).
- **Element Picker**: Provides "Select Element" functionality with visual highlighting.

### 2.2 The Elements Panel (`ElementsPanel.cs`)

A fully interactive DOM tree visualizer.

- **Features**:
  - Live DOM Tree navigation.
  - CRUD operations on Attributes and Text.
  - Real-time CSS Computed Style inspection (`FindPropertyOrigin` traces rules).
  - Box Model visualization (Margin/Border/Padding diagrams).

---

## 3. Remote Debugging Protocol (`Core/RemoteDebugServer.cs`)

FenBrowser implements a TCP Server on port `9222` to support the **Chrome DevTools Interface**.

### 3.1 Architecture

- **Transport**: WebSocket (RFC 6455) with support for 64-bit frames.
- **Discovery**: Exposes `/json/version` and `/json/list` HTTP endpoints for client discovery.

### 3.2 Supported Domains (`Domains/`)

- **DOM**: Querying nodes, requesting child nodes, highlighting.
- **DOM Editing**: Attribute updates, text-node edits, and parsed `outerHTML` replacement through the protocol-backed Elements workflow.
- **CSS**: modifying stylesheets, computing styles.
- **Network**: Request/Response monitoring (Basic).
- **Runtime**: JS evaluation and console logging.

## 4. Integration

The DevTools bridge to the rest of the system via `IDevToolsHost`. This allows the tools to be decoupled from the specific `FenBrowser.Host` implementation, facilitating testing and headless operation.

---

## 5. Comprehensive Source Encyclopedia

This section maps **every key file** in the DevTools library, covering the Native UI and Remote Debugging Server.

### 5.1 Native UI Panels (`FenBrowser.DevTools.Panels`)

#### `ElementsPanel.cs` (Lines 1-2034)

The comprehensive DOM visualization and editing suite.

- **Lines 438-642**: **`DrawDomTree`**: The recursive rendering engine for the DOM tree, handling indentation and expansion.
- **Lines 1741-1785**: **`Search`**: Implements global search for nodes by tag, ID, or content.
- **Lines 118-173**: **`HandleProtocolEvent`**: Updates the view in response to engine events.

#### `ConsolePanel.cs` (Lines 1-423)

Displays JavaScript console logs and errors.

- **Lines 100-300**: **`DrawLogEntry`**: Renders text with syntax highlighting for objects/arrays.
- **Lines 400-450**: **`EvaluateInput`**: REPL implementation sending commands to the Engine.

#### `NetworkPanel.cs` (Lines 1-351)

Visualizes network requests (Waterfall, Timing, Headers).

- **UpdateList**: Subscribes to `BrowserHost.NetworkEvents`.
- **DrawWaterfall**: Renders the timing bars (DNS/Connect/SSL/Wait/Download).

### 5.2 Core Infrastructure (`FenBrowser.DevTools.Core`)

#### `RemoteDebugServer.cs` (Lines 1-478)

The WebSocket implementation of the Chrome DevTools Protocol.

- **Lines 260-378**: **`ReceiveWebSocketLoop`**: Handles frame decoding and masking for incoming WebSocket messages.
- **Lines 80-110**: **`SendPingToAllClients`**: Maintains connection health via heartbeats.
- **Lines 184-234**: **`HandleHttpRequest`**: Serves discovery JSON endpoints.

### 5.3 Controller & Utilities

#### `DevToolsController.cs` (Lines 1-200+)

Manages panel lifecycle and layout (Docked/Undocked state).

#### `Core/Protocol/`

Contains the generated CDP domain classes and DTOs.

### 5.3 Supplemental Files (Gap Fill)

#### Interfaces (`FenBrowser.DevTools`)

- **`IDevToolsHost.cs`**: Abstract interface allowing DevTools to control the Browser Host.
- **`IDevToolsPanel.cs`**: Common interface for all UI panels (`Console`, `Elements`).
- **`DevToolsWidget.cs`**: The main container widget hosting the panels.

#### Protocol Definitions (`FenBrowser.DevTools.Core.Protocol`)

- **`Protocol/ProtocolMessage.cs`**: Sub-classes representing `Request`, `Response`, and `Event` notification wrappers.
- **`Domains/*.cs`**: generated handlers for each CDP domain (e.g., `DomDomain.cs`).

#### Utils

- **`Protocol/ProtocolMessage.cs`**: serialization logic using `JsonSerializerOptions`.
- **`RemoteDebugServer.cs`**: Low-level WebSocket framing logic.

### 5.4 Phase-0 Security Hardening (2026-02-18)

- `Core/RemoteDebugServer.cs`
  - Remote debug transport now supports explicit bind address and optional authentication token.
  - Unauthorized requests are rejected with HTTP `401`.
  - Discovery endpoints now emit host/port from runtime configuration.

- `FenBrowser.Host/ChromeManager.cs`
  - Remote debugging is now **disabled by default**.
  - Enable explicitly via environment variables:
    - `FEN_REMOTE_DEBUG=1`
    - `FEN_REMOTE_DEBUG_PORT` (optional)
    - `FEN_REMOTE_DEBUG_BIND` (optional, defaults `127.0.0.1`)
    - `FEN_REMOTE_DEBUG_TOKEN` (optional, recommended)

### 5.5 Eight-Gap Closure Tranche - Cookie Source-of-Truth Wiring (2026-02-19)

- `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`
  - Added cookie bridge delegates:
    - `CookieSnapshotProvider`
    - `CookieSetter`
    - `CookieDeleteHandler`
    - `CookieClearHandler`
  - DevTools cookie APIs now use injected browser cookie source when available, with local list fallback for standalone contexts.

- `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
  - Wired DevTools cookie delegates to `CustomHtmlEngine` cookie jar-backed operations.

### 5.6 Completion Pass - Debugger/Host Wiring Closure (2026-02-19)

- `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`
  - `ShouldPause(...)` now evaluates conditional breakpoints via runtime expression evaluation.
  - Added explicit truthiness interpretation helper for condition results.

- `FenBrowser.Host/DevToolsHostAdapter.cs`
- `FenBrowser.Host/BrowserIntegration.cs`
  - DevTools `ScrollToElement` now routes through host adapter to viewport-centered scroll behavior in browser integration.



### 5.7 Async Runtime Evaluation Wiring (2026-03-06)

- `FenBrowser.DevTools/Core/IDevToolsHost.cs`
  - DevTools host evaluation contract is now asynchronous (`EvaluateScriptAsync(...)`).
- `FenBrowser.DevTools/Domains/RuntimeDomain.cs`
  - `Runtime.evaluate` now awaits the host evaluation path instead of assuming a synchronous result.
- `FenBrowser.Host/DevToolsHostAdapter.cs`
  - Adapter now forwards `Runtime.evaluate` through the async host bridge, preserving non-blocking protocol handling.
- Net effect:
  - DevTools runtime evaluation no longer depends on a synchronous host timeout bridge.
  - Protocol evaluation stays responsive while the page script executes asynchronously.


### 5.8 Async DOM/CSS Access Dispatch (2026-03-06)

- `FenBrowser.DevTools/Core/DevToolsServer.cs`
  - `InitializeDom(...)` and `InitializeCss(...)` now accept an optional async dispatcher for host-owned access.
- `FenBrowser.DevTools/Domains/DomDomain.cs`
  - All DOM reads and writes now execute through the injected dispatcher when present, covering:
    - `getDocument`
    - `highlightNode` / `hideHighlight`
    - `requestChildNodes`
    - `setAttributeValue`
    - `removeAttribute`
    - `setNodeValue`
- `FenBrowser.DevTools/Domains/CSSDomain.cs`
  - CSS inspection and style-edit requests now execute through the injected dispatcher before touching host-owned DOM/CSS state.
- Net effect:
  - DevTools domain handlers remain async end-to-end.
  - Cross-thread DOM/CSS access from protocol requests is routed back to the owning host thread instead of executing directly on the transport thread.

### 5.9 DOM Markup Replacement Baseline (2026-03-28)

- `FenBrowser.DevTools/Domains/DomDomain.cs`
  - Added `DOM.setOuterHTML` support so protocol clients can replace a selected node with parsed markup instead of falling through to an unknown-method failure.
  - Replacement markup is parsed through the existing core HTML parser and applied as a `DocumentFragment`, which keeps node insertion/removal flowing through the standard DOM mutation path.
  - Empty markup now removes the selected node.

- `FenBrowser.DevTools/Instrumentation/DomInstrumenter.cs`
- `FenBrowser.DevTools/Domains/DTOs/DomNodeDto.cs`
  - DOM snapshots and mutation events now preserve comment-node identity (`nodeType = 8`, `nodeName = "#comment"`), which matters when markup edits introduce or remove comments.

- `FenBrowser.Tests/DevTools/DomDomainTests.cs`
  - Regression coverage now verifies that `DOM.setOuterHTML` still waits for the owning-thread dispatcher and that parsed replacement nodes are visible to subsequent inspection.

### 5.10 DOM Query + Exact Markup Editing Expansion (2026-03-28)

- `FenBrowser.DevTools/Domains/DomDomain.cs`
  - Added selector-driven subtree inspection methods:
    - `DOM.querySelector`
    - `DOM.querySelectorAll`
  - Added exact markup retrieval via `DOM.getOuterHTML`.
  - Markup serialization now preserves document, document-fragment, element, text, comment, and doctype shapes instead of forcing the Elements panel to reconstruct lossy placeholder HTML.

- `FenBrowser.DevTools/Domains/DTOs/DomNodeDto.cs`
  - Added protocol DTOs for:
    - `GetOuterHtmlResult`
    - `QuerySelectorResult`
    - `QuerySelectorAllResult`

- `FenBrowser.DevTools/Panels/ElementsPanel.cs`
  - The native Elements panel `Edit as HTML` path now hydrates from protocol-backed `DOM.getOuterHTML` rather than the old local placeholder serializer.
  - Added a dedicated modal editor overlay with explicit commit/cancel flow:
    - `Ctrl+Enter` applies the edited markup through `DOM.setOuterHTML`
    - `Enter` inserts a newline
    - `Esc` cancels editing
  - Net effect:
    - The in-process inspector now edits exact serialized markup.
    - External protocol clients and the native panel share the same DOM-markup source of truth.

- `FenBrowser.Tests/DevTools/DomDomainTests.cs`
  - Regression coverage now verifies:
    - `DOM.getOuterHTML`
    - `DOM.querySelector`
    - `DOM.querySelectorAll`
    - async dispatcher preservation for the new methods.

### 5.11 Network Payload Inspection Baseline (2026-03-28)

- `FenBrowser.DevTools/Domains/NetworkDomain.cs`
  - Added basic retrieval methods on top of the existing event stream:
    - `Network.getResponseBody`
    - `Network.getRequestPostData`
  - These methods resolve against the host-tracked request ledger instead of remaining a no-op `enable/disable` shell.

- `FenBrowser.DevTools/Core/IDevToolsHost.cs`
- `FenBrowser.Host/DevToolsHostAdapter.cs`
  - `NetworkRequestInfo` now carries both request and response bodies.
  - Host-side network request translation now preserves `RequestBody` / `ResponseBody` from the engine and maps common HTTP status codes to reason phrases (`200 OK`, `404 Not Found`, etc.) for better parity with real DevTools network views.

- `FenBrowser.DevTools/Panels/NetworkPanel.cs`
  - The native Network panel now fetches protocol-backed payload previews when a request is selected and renders:
    - request body preview
    - response body preview
  - This closes the old gap where the panel only exposed headers and timing metadata despite the engine already buffering bodies.

- `FenBrowser.Tests/DevTools/NetworkDomainTests.cs`
  - Regression coverage now verifies:
    - `Network.getResponseBody`
    - `Network.getRequestPostData`
    - unavailable-body failure behavior.

### 5.12 Runtime Object Inspection + Source Surfacing (2026-03-28)

- `FenBrowser.DevTools/Domains/RuntimeDomain.cs`
- `FenBrowser.DevTools/Domains/DTOs/RuntimeDtos.cs`
  - Added `Runtime.getProperties` and a remote-object handle store so non-primitive evaluation results stop collapsing into opaque `ToString()` output.
  - `Runtime.evaluate` now shapes primitives, JSON values, dictionaries/lists, and FenEngine `FenValue` / `IObject` results into protocol-grade `RemoteObject` descriptors with stable `objectId` handles for follow-up inspection.

- `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`
- `FenBrowser.DevTools/Core/IDevToolsHost.cs`
- `FenBrowser.Host/DevToolsHostAdapter.cs`
  - Script registration now preserves stable `ScriptId` values for the same URL instead of minting a fresh identifier on every re-registration.
  - The host adapter now exposes the engine-owned script registry to DevTools, including protocol-facing `ScriptId` values and inline-source detection.

- `FenBrowser.DevTools/Domains/DebuggerDomain.cs`
- `FenBrowser.DevTools/Domains/DTOs/DebuggerDtos.cs`
- `FenBrowser.DevTools/Core/DevToolsServer.cs`
  - Added a first real `Debugger` domain surface:
    - `Debugger.enable`
    - `Debugger.disable`
    - `Debugger.getScriptSource`
  - `Debugger.enable` now emits `Debugger.scriptParsed` for already-registered page scripts so protocol clients can build a usable source tree instead of seeing an empty debugger surface.

- `FenBrowser.DevTools/Panels/ConsolePanel.cs`
  - The in-process console now uses `Runtime.getProperties` to format object and array evaluation results into actual property previews rather than printing only the top-level description string.

- `FenBrowser.DevTools/Panels/SourcesPanel.cs`
- `FenBrowser.Host/ChromeManager.cs`
  - Added a native Sources panel to the built-in DevTools shell.
  - The panel subscribes to `Debugger.scriptParsed`, lists loaded scripts, and fetches source text through `Debugger.getScriptSource`, giving Fen its first end-to-end source inspection workflow.

- `FenBrowser.Tests/DevTools/RuntimeDomainTests.cs`
- `FenBrowser.Tests/DevTools/DebuggerDomainTests.cs`
  - Regression coverage now verifies:
    - `Runtime.getProperties` for object evaluation results
    - `Debugger.scriptParsed` emission on enable
    - `Debugger.getScriptSource` source retrieval

### 5.13 Audit-Fix Tranche - Host Lifetime, Target Scoping, Pane Isolation (2026-03-28)

- `FenBrowser.DevTools/Core/IDevToolsPanel.cs`
  - Panel host assignment now has an explicit `OnHostChanging(...)` lifecycle hook.
  - This gives each panel a deterministic place to detach protocol/log listeners and clear target-owned UI state before a tab switch reattaches the panel to a new host.

- `FenBrowser.DevTools/Core/DevToolsServer.cs`
  - JSON output listeners are now guarded by a lock and can be removed via `RemoveJsonOutput(...)`.
  - Broadcast now snapshots listeners before iterating, which prevents append-only relay growth from compounding across host churn.

- `FenBrowser.Host/DevToolsHostAdapter.cs`
- `FenBrowser.Host/ChromeManager.cs`
  - DevTools hosts are now disposable and are explicitly disposed before a new tab host is attached.
  - Disposal detaches:
    - browser repaint relay
    - browser console relay
    - engine network relay
    - protocol JSON relay
  - The debugger/source bridge no longer exposes the entire global script registry blindly.
    - `GetScriptSources()` now scopes the returned scripts to the active inspected document by walking live `<script>` elements, resolving external `src` URLs relative to the current page, and synthesizing stable inline IDs/URLs when needed.
    - `eval.js` sources remain visible as explicit evaluation artifacts, but stale page scripts from unrelated tabs or prior navigations no longer dominate the Sources surface.

- `FenBrowser.DevTools/Panels/ElementsPanel.cs`
- `FenBrowser.DevTools/Panels/ConsolePanel.cs`
- `FenBrowser.DevTools/Panels/NetworkPanel.cs`
- `FenBrowser.DevTools/Panels/SourcesPanel.cs`
  - The core panels now unsubscribe from old hosts during host transitions instead of stacking listeners indefinitely.
  - Host changes also clear selection, hover, preview, and scroll state so a newly inspected target does not inherit stale UI state from the previous one.

- `FenBrowser.DevTools/Panels/NetworkPanel.cs`
- `FenBrowser.DevTools/Panels/SourcesPanel.cs`
  - Split-pane rendering no longer relies on the base panel’s whole-canvas scroll translation.
  - Each panel now paints its own panes and scrollbars so left/list panes scroll independently from right/detail/source panes.
  - The Network details loader now snapshots the selected request ID and refuses to overwrite previews when a slower async body fetch completes after the user has already selected another request.

- `FenBrowser.DevTools/Panels/ConsolePanel.cs`
  - Protocol-backed console entry formatting is now serialized through a gate instead of fire-and-forget per argument.
  - This keeps object-preview formatting in arrival order and prevents older host work from writing into the panel after a target switch.

- `FenBrowser.DevTools/Core/DevToolsController.cs`
  - DevTools visibility changes and panel activation now reset the host cursor to `Default` immediately.
  - This closes the stale-cursor path where switching away from a text-driven panel (for example the Console input) could leave the I-beam cursor visible until the next mouse-move event.

- `FenBrowser.Host/ChromeManager.cs`
  - Resize drag teardown now reapplies the pending host cursor on mouse-up instead of waiting for the next hover update.
  - This closes the stale resize-cursor path where DevTools chrome resizing could leave the non-default cursor visible after the drag had already ended.

- `FenBrowser.FenEngine/DOM/EventTarget.cs`
- `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
  - DOM input dispatch now contains page-script failures at the browser boundary.
  - `on*` handler-property callbacks (for example `onmousemove`) now mirror `addEventListener(...)` listeners:
    - exceptions are logged
    - `window.onerror` reporting is attempted
    - the exception does not escape into the host message loop
  - `BrowserApi.DispatchInputEvent(...)` now treats uncaught page faults and script timeouts as logged page errors rather than process-terminating host exceptions, which hardens interactive paths such as page hover handlers triggered while resizing DevTools.

- `FenBrowser.DevTools/Domains/RuntimeDomain.cs`
- `FenBrowser.DevTools/Domains/DTOs/RuntimeDtos.cs`
  - `Runtime` remote-object retention is now bounded and externally releasable.
  - Added:
    - `Runtime.releaseObject`
    - `Runtime.releaseObjectGroup`
  - The runtime now caps retained remote objects and clears the cache on `Runtime.disable`, preventing unbounded growth from repeated evaluations/property inspection.

- `FenBrowser.Tests/DevTools/RuntimeDomainTests.cs`
- `FenBrowser.Tests/DevTools/DevToolsServerTests.cs`
  - Regression coverage now verifies:
    - `Runtime.getProperties` returns inspectable own properties for object results
    - `Runtime.releaseObject` removes retained handles
    - `DevToolsServer.RemoveJsonOutput(...)` stops future broadcasts from reaching removed listeners

Net effect:
- Tab switches stop duplicating protocol/log events.
- Sources/debugger inspection is materially closer to the active target instead of the process-global script pool.
- Network and Sources panes behave like real split views instead of one shared scroller.
- Console/runtime object inspection no longer grows retained state without a release path.

### 5.14 Remote Debug Bind Policy And Structured Correlation (2026-03-29)

- `FenBrowser.DevTools/Core/RemoteDebugServer.cs`
  - Remote debug bind selection now routes through `FenBrowser.Core.Security.BrowserSecurityPolicy.EvaluateRemoteDebugBinding(...)`.
  - Loopback remains the default allowed bind surface.
  - Non-loopback binding now requires explicit operator override via `FEN_REMOTE_DEBUG_ALLOW_REMOTE=1`.
- The remote debug server now emits `DevTools` and `Security` category logs with ambient scope metadata for bind rejection, listener startup, client session handling, and request-path failures.
- Why this mattered:
  - A production browser cannot treat remote-debug exposure as an ad hoc TCP convenience toggle.
  - The deny path must be explicit, logged, and consistent with the rest of the browser security boundary model.
- Verification:
  - `dotnet build FenBrowser.DevTools/FenBrowser.DevTools.csproj -nologo` completed successfully on `2026-03-29`.

### 5.15 Protocol Contract Hardening (2026-03-29)

- `FenBrowser.DevTools/Core/Protocol/ProtocolMessage.cs`
  - Protocol JSON parsing now treats malformed payloads as parse failures instead of surfacing serializer exceptions into transport flow.

- `FenBrowser.DevTools/Core/Protocol/MessageRouter.cs`
  - Duplicate domain registration is now rejected explicitly.
  - Event subscription is now idempotent.
  - Protocol failure codes now distinguish:
    - invalid request (`-32600`)
    - method/domain not found (`-32601`)
    - internal handler failure (`-32603`)
    - malformed JSON parse failure (`-32700`)

- `FenBrowser.DevTools/Instrumentation/DomInstrumenter.cs`
  - The DOM instrumenter now detaches its global mutation subscription on disposal instead of leaking a permanent static-event listener across host/controller lifetimes.
  - Mutation callback signatures now align with the nullable DOM mutation event contract, and attribute-change forwarding now guards absent attribute names.

- `FenBrowser.Tests/DevTools/MessageRouterTests.cs`
  - Added regressions proving malformed JSON is returned as a protocol parse error and that duplicate domain registration is rejected.

_End of Volume V_
