# FenBrowser Codex - Volume V: Developer Tools

**State as of:** 2026-02-06
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

_End of Volume V_
