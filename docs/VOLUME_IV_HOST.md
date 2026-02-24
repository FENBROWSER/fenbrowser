# FenBrowser Codex - Volume IV: The Host Application

**State as of:** 2026-02-18
**Codex Version:** 1.0

## 1. Overview

`FenBrowser.Host` is the executable entry point that users interact with. It provides the operating system window, handles native inputs, and hosts the rendering surface. Unlike traditional C# apps (WPF/WinForms), FenBrowser uses a **custom high-performance rendering stack** based on Silk.NET and SkiaSharp.

## 2. Technology Stack

- **Windowing**: [Silk.NET](https://github.com/dotnet/Silk.NET) (GLFW backend) provides cross-platform window creation and input handling.
- **Graphics Context**: OpenGL via Silk.NET.
- **2D Rendering**: [SkiaSharp](https://github.com/mono/SkiaSharp) (Hardware Accelerated) renders directly to the OpenGL context.
- **CLI**: Custom argument parsing for specialized modes.

## 3. Architecture

### 3.1 The Entry Point (`Program.cs`)

The application bootstraps in `Main(string[] args)`.

- **Modes**:
  - `Default`: Launches the UI options with the given URL.
  - `--headless`: runs without a window (for tests).
  - `--test262`: Runs the ECMA-262 compliance suite.
  - `--wpt`: Runs the Web Platform Tests adapter.
  - `--acid2`: Specialized Acid2 test runner.
- **High-DPI**: Enforces `PerMonitorV2` awareness on Windows via P/Invoke.

### 3.2 WindowManager (`WindowManager.cs`)

A Singleton that manages the native window lifecycle.

- **Surface Creation**:
  1. Creates an OpenGL Context (`GRGlInterface`).
  2. Wraps it in a Skia `GRContext`.
  3. Creates an `SKSurface` backed by the OpenGL Framebuffer (`GRBackendRenderTarget`).
- **Input Proxying**: Bridges Silk.NET input events (Keyboard/Mouse) to the internal `InputManager`.
- **Main Loop**: Drives the application refresh loop (`Window.Render` event).

### 3.3 The Integration Layer (`BrowserIntegration.cs`)

This class acts as the "Glue" between the Host and the Engine.

- **Coordinate Systems**: Translates between **Window Space** (Physical Pixels), **UI Space** (Logical Pixels), and **Document Space** (Scroll-offset Pixels).
- **Render Loop**:
  - `RecordFrame()`: Engine produces a paint tree (Background thread).
  - `Render()`: Host draws the paint tree to the canvas (UI thread).
- **Input Resilience (2026-02-07)**:
  - Click activation/link handling fallback is executed in the authoritative `HandleMouseUp(... emitClick)` path using the same hit-test result (`result.NativeElement`), so web-content pointer routing remains correct even when `HandleClick(...)` is bypassed by widget-level down/up flow.
  - `HandleMouseUp(... emitClick)` now falls back to the last stable hover hit when release-time hit test is transiently null, so links/controls still activate during pointer-target flicker.
  - Host pointer routing now uses explicit `HandleMouseDown(...)` + `HandleMouseUp(... emitClick)` for web content, with click emitted on mouse-up.
  - Mouse-move routing for web content is centralized in `ChromeManager` to avoid duplicate move dispatch paths.

## 4. UI System (`Widgets/`, `ChromeManager.cs`)

FenBrowser does not use standard UI controls. It renders its own UI (Tabs, URL Bar, Buttons) using the same Skia pipeline as the web content.

- **ChromeManager**: Manages the browser chrome (UI) layout.
- **MsgPass**: A simple messaging system for UI events.

## 5. Development Features

- **Crash Handling**: Global exception handlers for Unobserved Tasks and Domain exceptions.
- **Crashes**: Logs strictly to `FENBROWSER/logs`.
- **Debug Overlays**: Supports drawing debug information (FPS, Layer Borders) directly on the canvas.

---

## 6. Comprehensive Source Encyclopedia

This section maps **every key file** in the Host application.

### 6.1 Integration Layer (`FenBrowser.Host`)

#### `BrowserIntegration.cs` (Lines 1-1258)

The vital bridge connecting the Engine's `BrowserHost` to the Silk.NET UI loop.

- **Lines 876-923**: **`PerformHitTest`**: Transforms Window coordinates to Document coordinates (handling DPI and scroll).
- **Lines 430-456**: **`Render`**: Draws the current display list to the UI canvas.
- **Lines 336-404**: **`NavigateAsync`**: Triggers navigation and updates UI state.

#### `ChromeManager.cs` (Lines 1-557)

Manages the "Chrome" (UI outside the web content).

- **Lines 155-173**: **`InitializeDevTools`**: Bootstraps the DevTools system.
- **Lines 285-304**: **`OnActiveTabChanged`**: Updates the URL bar and tab strip when switching tabs.
- **Lines 385-411**: **`OnKeyDown`**: Routes global hotkeys (Ctrl+T, Ctrl+W).

### 6.2 Application Entry

#### `Program.cs` (Lines 1-530)

The application entry point.

- **Lines 19-506**: **`Main`**: Bootstraps `WindowManager` and `ChromeManager`, handling CLI args.
- **Lines 520-526**: **`CopyToClipboard`**: Platform-specific clipboard bridging.

#### `WindowManager.cs` (Lines 1-418)

Manages the Silk.NET window and input context.

- Handles Window creation, resizing, and raw input dispatching to the `InputManager`.

### 6.3 UI Widgets (`FenBrowser.Host.Widgets`)

#### `SettingsPageWidget.cs` (Lines 1-1358)

The browser's internal settings page (`fen://settings`).

- **Lines 111-469**: **`InitializeControls`**: Builds the massive UI tree for settings.
- **Lines 746-1138**: **`Paint`**: Custom rendering logic for the settings interface.

#### `TabBarWidget.cs` (Lines 1-400) & `TabWidget.cs` (Lines 1-250)

The visual implementation of the browser tabs.

- **TabWidget**: Renders the individual tab shape (trapezoid/rounded), title, and close button.
- **TabBarWidget**: Manages the collection of tabs, scrolling, and dragging logic.

#### `ToolbarWidget.cs` (Lines 1-200)

Container for the Back/Forward buttons and Address Bar.

#### `WebContentWidget.cs` (Lines 1-300)

The viewport container that hosting the rendered `SKPicture` from the engine.

- Bridges mouse/keyboard IO from the Host to the Engine's `InputManager`.

#### `StatusBarWidget.cs` (Lines 1-150)

Displays hover link URLs and loading status at the bottom of the window.

#### `BookmarksBarWidget.cs` (Lines 1-150)

Rendering of the bookmark icons below the address bar.

#### `ContextMenuWidget.cs` (Lines 1-250) & `DropdownWidget.cs` (Lines 1-250)

Overlay primitives for popups.

- **ContextMenu**: Right-click menus.
- **Dropdown**: Select box options and autocomplete lists.

#### `ButtonWidget.cs` (Lines 1-180)

Standard skinnable button control (Hover/Active states).

#### `StackPanel.cs` (Lines 1-80) & `DockPanel.cs` (Lines 1-120)

Layout containers for arranging child widgets.

#### `InspectorPopupWidget.cs` (Lines 1-200)

The container window for the undocked DevTools.

#### `SiteInfoPopupWidget.cs` (Lines 1-300)

The "Lock Icon" popup showing SSL certificate details and cookies.

#### `SwitchWidget.cs` (Lines 1-100)

Toggle switch UI (used in Settings).

#### `TextInputWidget.cs` (Lines 1-400)

Base class for text entry fields (cursor rendering, selection handling).

#### `AddressBarWidget.cs` (Lines 1-675)

The Omnibox implementation.

- **Lines 605-624**: **`RequestAutocomplete`**: Triggers history/bookmark suggestion logic.
- **Lines 650-662**: **`GetSecurityIconColor`**: Visualizes SSL/TLS state (Green/Red/Gray).

### 6.4 Supplemental Files (Gap Fill)

#### Input & Windowing (`FenBrowser.Host.Input`)

- **`InputManager.cs`**: Aggregates raw Silk.NET events and dispatches them to widgets/web content.
- **`KeyboardDispatcher.cs`**: Translates scancodes to virtual keys and handles shortcuts.
- **`CursorManager.cs`**: Changes the mouse cursor (Text, Pointer, Hand).
- **`FocusManager.cs`**: Tracks which widget currently holds keyboard focus.

#### Infrastructure (`FenBrowser.Host`)

- **`Compositor.cs`**: Manages the OpenGL context swapping and VSync.
- **`RootWidget.cs`**: The top-level container for the entire window UI.
- **`ThemeManager.cs`**: Loads logic for Light/Dark mode colors.
- **`DevToolsHostAdapter.cs`**: Adapter for docking the DevTools window found in `FenBrowser.DevTools`.

#### Testing/Driver (`FenBrowser.Host.Driver`)

- **`FenBrowserDriver.cs`**: The external-facing WebDriver API implementation.
- **`HostBrowserDriver.cs`**: Internal hooks for automated testing.

### 6.5 Phase-0 Security Hardening (2026-02-18)

- `ChromeManager.cs`
  - Remote debug startup is now opt-in (`FEN_REMOTE_DEBUG=1`) with configurable port/bind/token values.
  - WebDriver startup is now opt-in (`FEN_WEBDRIVER=1`) with optional `FEN_WEBDRIVER_PORT`.
  - Host-to-WebDriver driver wiring now uses direct `WebDriverServer.SetDriver(...)` instead of reflection over private fields.

### 6.6 Phase-1 CLI Normalization (2026-02-18)

- `Program.cs`
  - Removed the duplicate `--wpt` command branch to keep a single authoritative WPT entrypoint.
  - Reduced command-routing ambiguity in CLI startup flow by consolidating WPT handling into one path.

### 6.7 Phase-2 Default Path Hygiene (2026-02-18)

- `Program.cs`
  - Removed machine-specific absolute default paths for test suites.
  - Default Test262 root now resolves to `Path.Combine(Directory.GetCurrentDirectory(), "test262")`.
  - Default WPT root now resolves to `Path.Combine(Directory.GetCurrentDirectory(), "wpt")`.

### 6.8 Phase-5 Host Integration Completion (2026-02-18)

- `WebDriver/FenBrowserDriver.cs`
- `WebDriver/HostBrowserDriver.cs`
  - Expanded host WebDriver adapters to implement the full command surface used by `FenBrowser.WebDriver` (window context, element-state, cookies, actions, alerts, print, and element screenshot operations).
  - Driver-side behavior now routes through `BrowserHost` APIs instead of placeholder-only paths.

- `Widgets/SettingsPageWidget.cs`
  - Privacy settings UI keeps runtime-wired controls visible (`Safe Browsing`, `Block pop-ups`).
  - `ImproveBrowser` remains hidden in privacy UI until broader UX policy for telemetry controls is finalized.

### 6.9 Remaining Findings Tranche - Navigation Intent Split (2026-02-19)

- `BrowserIntegration.cs`
  - Split navigation entrypoints into:
    - user-input navigation (address-bar flow)
    - programmatic navigation (WebDriver/script/callback flow).
  - Programmatic local-path normalization is now blocked and delegated to engine policy gates.

- `Tabs/BrowserTab.cs`
  - Added explicit programmatic navigation method for automation pathways.

- `WebDriver/FenBrowserDriver.cs`
- `WebDriver/HostBrowserDriver.cs`
  - Updated WebDriver navigation wiring to use programmatic navigation path only.

### 6.10 Phase-Completion Tranche - Secure DNS UI Wiring (2026-02-19)

- `Widgets/SettingsPageWidget.cs`
  - Re-enabled `Use Secure DNS` toggle visibility in privacy settings after runtime DoH transport wiring landed in core network stack.

### 6.11 Eight-Gap Closure Tranche - Process Model and Sync-Wait Cleanup (2026-02-19)

- `ProcessIsolation/IProcessIsolationCoordinator.cs` (new)
- `ProcessIsolation/InProcessIsolationCoordinator.cs` (new)
- `ProcessIsolation/ProcessIsolationCoordinatorFactory.cs` (new)
  - Added explicit process-model coordination seam for host runtime with environment selection.
  - Environment hook: `FEN_PROCESS_ISOLATION=brokered` enables brokered per-tab renderer child-process mode.

- `ChromeManager.cs`
  - Host startup now initializes process isolation coordinator and tracks tab creation/activation through it.
  - Replaced direct `.Result`/`.Wait()` UI dispatch callsites with centralized synchronous UI bridge helpers (`GetAwaiter().GetResult()` pattern).

- `BrowserIntegration.cs`
  - Replaced timeout path based on `Task.Wait(...)`/`.Result` with `Task.WhenAny(...)` + `GetAwaiter().GetResult()` bridge for legacy sync API compatibility.

### 6.12 Completion Pass - Brokered Runtime and Stability Wiring (2026-02-19)

- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs` (new)
  - Added concrete brokered process coordinator:
    - per-tab renderer child process launch
    - tab-close cleanup
    - shutdown cleanup.

- `Program.cs`
  - Added renderer-child startup mode:
    - CLI arg: `--renderer-child`
    - env flag: `FEN_RENDERER_CHILD=1`
  - Renderer child loop monitors parent process lifetime and exits cleanly when parent terminates.

- `ChromeManager.cs`
  - Added tab-close and shutdown calls into process-isolation coordinator (`OnTabClosed`, `Shutdown`).
  - Favorites button now toggles favorites-bar visibility and repaints root UI (removed placeholder TODO path).

- `Compositor.cs`
  - Added offscreen frame-buffer composition path to reduce visible backbuffer blink/flicker.

- `BrowserIntegration.cs`
- `DevToolsHostAdapter.cs`
  - Added `ScrollToElement(...)` bridge path from DevTools host adapter into browser integration viewport scrolling.

### 6.13 Final Process-Isolation Closure - Authenticated IPC Routing (2026-02-19)

- `ProcessIsolation/RendererIpc.cs` (new)
- `ProcessIsolation/RendererInputEvent.cs` (new)
- `ProcessIsolation/ProcessIsolationRuntime.cs` (new)
  - Added typed IPC envelope/payload model for brokered renderer communication.
  - Added per-tab named-pipe session with:
    - current-user-only pipe access
    - per-session auth token handshake
    - lifecycle + ack/error/frame-ready read loop.

- `ProcessIsolation/IProcessIsolationCoordinator.cs`
- `ProcessIsolation/InProcessIsolationCoordinator.cs`
- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Extended isolation coordinator contract with event channels:
    - `OnNavigationRequested(...)`
    - `OnInputEvent(...)`
    - `OnFrameRequested(...)`.
  - Brokered coordinator now owns session lifecycle and forwards tab events to child process via IPC.

- `Tabs/BrowserTab.cs`
- `Widgets/WebContentWidget.cs`
- `ChromeManager.cs`
  - Host now emits navigation/input/frame requests through the process-isolation runtime coordinator.
  - Coordinator registration/teardown is now explicit in host lifecycle.

- `Program.cs`
  - Renderer child mode now runs authenticated IPC command loop.
  - Child side executes routed navigation/input operations using isolated `BrowserHost` instance.
  - Parent liveness monitoring remains enforced to prevent orphan renderer children.
  - Current frame channel is heartbeat/metadata (`FrameReady`); direct pixel transport is intentionally deferred.

### 6.14 Process-Isolation Hardening Tranche ISO-1 (2026-02-20)

- `Core/ProcessIsolation/RendererIsolationPolicies.cs` (new)
  - Added deterministic origin-assignment policy primitives:
    - `OriginIsolationPolicy.TryGetAssignmentKey(...)`
    - `OriginIsolationPolicy.RequiresReassignment(...)`
  - Added bounded crash-restart policy:
    - `RendererRestartPolicy` (max attempts + exponential backoff + cap).

- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Added per-tab isolation state tracking:
    - assignment key
    - last navigation replay data
    - active/expected child PID
    - restart-attempt accounting.
  - Added origin-strict process reassignment on cross-origin top-level navigation.
  - Added child-process crash detection (`Process.Exited`) with bounded restart and backoff policy.
  - Added renderer startup environment policy wiring:
    - `FEN_RENDERER_SANDBOX_PROFILE=renderer_minimal`
    - `FEN_RENDERER_CAPABILITIES=navigate,input,frame`
    - `FEN_RENDERER_ASSIGNMENT_KEY=<origin-key>`.

- `Program.cs`
  - Added renderer-child startup assertions for sandbox profile and capability set.
  - Added assignment-key startup logging.
  - Frame request handler now returns explicit frame metadata:
    - `surfaceWidth`, `surfaceHeight`, `dirtyRegionCount`, `hasDamage`.

- `ProcessIsolation/RendererIpc.cs`
  - Expanded `RendererFrameReadyPayload` with surface/damage metadata fields.
  - Host-side frame-ready logs now include surface and dirty-region metrics.

- `Tests/Core/RendererIsolationPoliciesTests.cs` (new)
  - Added policy regression coverage for:
    - origin-assignment key derivation/reassignment decisions
    - restart-budget limits
    - exponential backoff behavior.

### 6.15 Process-Isolation Gate Tranche ISO-2 (2026-02-20)

- `Core/ProcessIsolation/RendererIsolationPolicies.cs`
  - Added `RendererTabIsolationRegistry` policy state machine:
    - per-tab assignment state
    - navigation assignment decision (`requiresReassignment`)
    - expected/stale/unexpected exit classification
    - restart plan generation (delay + replay payload).

- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Coordinator now delegates policy decisions to the registry:
    - navigation isolation decisions
    - crash/exit decision handling
    - replay navigation after bounded restart.
  - Assignment transitions now log previous -> requested assignment keys through policy decision output.

- `Tests/Core/RendererIsolationPoliciesTests.cs`
  - Expanded to cover registry state machine behavior:
    - initial assignment
    - cross-origin reassignment
    - expected/stale exit classification
    - restart plan/replay data
    - budget exhaustion and shutdown/closed-tab behavior.

### 6.16 Process-Isolation Reliability Tranche ISO-3 (2026-02-20)

- `ProcessIsolation/RendererIpc.cs`
  - Added bounded disconnected-session outbound buffer for critical IPC envelopes.
  - Prevents startup race where navigation/control messages were dropped before pipe connection.
  - Explicitly excludes high-rate frame/input envelopes from buffering to avoid stale replay.

- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Added runtime knobs for restart/quarantine policy:
    - `FEN_RENDERER_STABLE_RESET_MS`
    - `FEN_RENDERER_CRASH_WINDOW_MS`
    - `FEN_RENDERER_MAX_CRASHES_IN_WINDOW`
    - `FEN_RENDERER_CRASH_QUARANTINE_MS`
  - Coordinator now gates child-process startup through policy registry (`CanStartSession`) and logs deny/retry state.
  - Exit-path logging now carries retry-after metadata when policy suppresses restart.

- `Core/ProcessIsolation/RendererIsolationPolicies.cs`
  - Expanded assignment mapping beyond network origins (`file/about/opaque` classes) to avoid accidental renderer reuse on scheme boundary transitions.
  - Added crash-loop quarantine and stable-runtime restart-attempt reset policy logic.

- `Tests/Core/RendererIsolationPoliciesTests.cs`
  - Added ISO-3 regressions:
    - opaque/local assignment derivation
    - stable-run restart reset
    - crash-loop quarantine decision + retryAfter semantics
    - user-input navigation quarantine release.

### 6.17 Navigation Lifecycle Tranche NL-1 (2026-02-20)

- `FenEngine/Rendering/BrowserApi.cs`
  - BrowserHost navigation now emits deterministic lifecycle transitions using core tracker:
    - `Requested -> Fetching -> ResponseReceived -> Committing -> Interactive -> Complete`
    - terminal `Failed` / `Cancelled`.
  - Stale navigation suppression added via navigation-id guard to avoid old in-flight completion clobbering newer navigations.
  - Removed timing/forced-event correctness hacks from the top-level navigation path:
    - removed forced `window.dispatchEvent(new Event('load'))`
    - removed delayed repaint fallback (`Task.Delay(1500)`).

- `BrowserIntegration.cs`
  - Host structured navigation events are now wired to lifecycle transitions:
    - `OnNavigationStarted`
    - `OnNavigationCompleted`
    - `OnNavigationFailed`.
  - This replaces passive placeholder events with deterministic runtime-fed signals.

- `Tests/Core/NavigationLifecycleTrackerTests.cs`
  - Added lifecycle state-machine regressions used by host lifecycle consumers.

### 6.18 Navigation Lifecycle Tranche NL-2 (2026-02-20)

- `FenEngine/Rendering/BrowserApi.cs`
  - Wired redirect-aware lifecycle metadata from `FetchResult` into `ResponseReceived` transitions.
  - Wired commit-source classification into `Committing` transitions.
  - Added bounded subresource/event-loop settle gate before `Complete` transition emission.

- `FenEngine/Rendering/ImageLoader.cs`
  - Added authoritative in-flight image-load accounting (`PendingLoadCount`) and pending-count change signal (`PendingLoadCountChanged`) used by host lifecycle completion gating.
  - Fixed pending-load removal to occur on all load exit paths (success/failure/early return), preventing stale completion blockers.

- `BrowserIntegration.cs`
  - Structured host navigation events now preserve lifecycle redirect classification (`IsRedirect`) for start/complete envelopes.

- `Tests/Core/NavigationLifecycleTrackerTests.cs`
  - Added regression case for redirect + commit-source metadata persistence.

### 6.19 Host Loading/Invalidation Reliability Tranche (2026-02-20)

- `BrowserIntegration.cs`
  - `LoadingChanged` now always marks repaint-needed, wakes the engine thread, and emits host repaint signal so loading/placeholder state cannot depend on incidental UI hover invalidations.
  - Engine wait strategy now avoids indefinite sleep while a tab is loading with no committed frame (`50ms` bounded wake path), preventing "stuck loading placeholder until input event" behavior.

- `ChromeManager.cs`
  - Active-tab wiring now subscribes to `LoadingChanged` and `TitleChanged` with explicit unsubscribe on tab switch.
  - Status bar loading state is now updated from active-tab loading transitions; window title now tracks active-tab title transitions without waiting for tab switch.

- `Widgets/TabWidget.cs`
  - Tab chrome now subscribes to tab state changes (`TitleChanged`, `LoadingChanged`, `NeedsRepaint`) instead of title-only invalidation.
  - Loading spinner now uses monotonic tick time and self-invalidates at bounded cadence while loading so animation is independent of mouse movement/hover.

- `Widgets/TabBarWidget.cs`
  - Added deterministic tab-widget event detachment on tab removal.
  - Removed extra `canvas.Restore()` call in tab-bar paint path to keep paint stack discipline strict.

_End of Volume IV_
