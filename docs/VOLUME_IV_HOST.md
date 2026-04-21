# FenBrowser Codex - Volume IV: The Host Application

**State as of:** 2026-03-30
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
- **Frame Transition Logging Guard (2026-04-13)**:
  - `BrowserIntegration.RecordFrame()` no longer emits `[TRANSITION]` layout logs on every repaint by default.
  - The transition log is now gated behind `DebugConfig.EnableDeepDebug && DebugConfig.LogFrameTiming`, removing high-frequency synchronous log pressure from normal navigation while preserving targeted tracing in deep debug runs.
- **Visited-State Bridge (2026-04-10)**:
  - `BrowserApi` now records successful request URLs into the shared `ElementStateManager` visited-url store so selector recascade can honor `:visited` / `:link` transitions driven by top-level and resource fetches.
  - The Host still drives recascade through the existing `ElementStateManager.OnStateChanged += _ => _engine.ScheduleRecascade()` subscription, so selector-state changes remain on the established host-engine handoff path rather than introducing a parallel invalidation channel.
- **Fragment Scroll Ordering Guard (2026-04-14)**:
  - `BrowserIntegration` now seeds pending fragment navigation from `_browser.CurrentUri` inside `RepaintReady` before requesting the first frame.
  - This closes an ordering race where `BrowserHost` emits `RepaintReady` before `Navigated`; previously `#fragment` targets (notably Acid2 `#top`) could miss the first frame-scroll application and remain at scroll origin.
- **Navigation Scroll Reset Guard (2026-04-14)**:
  - `BrowserIntegration.NavigateInternalAsync(...)` now resets `_scrollY` and `_contentHeight` at top-level navigation start and emits `ScrollChanged`.
  - This prevents stale scroll carry-over between pages (notably Acid2 `#top` -> `reference.html`) that could capture blank/offset first frames on short documents.
- **Wikipedia Stack-Overflow Guard (2026-04-18)**:
  - `BrowserIntegration.RecordFrame(...)` no longer seeds a new frame by drawing the previous committed `SKPicture` directly into the recorder.
  - Host base-frame seeding now uses a flattened raster snapshot (`SKImage`) of the last committed frame, which removes recursive/transitive picture nesting and prevents stack growth across long repaint sequences.
  - Reuse is explicitly bounded by age and consecutive-use limits before seeding, and navigation resets now dispose both `_currentFrame` and `_currentFrameSeedImage`.
- **Input Resilience (2026-02-07)**:
  - Click activation/link handling fallback is executed in the authoritative `HandleMouseUp(... emitClick)` path using the same hit-test result (`result.NativeElement`), so web-content pointer routing remains correct even when `HandleClick(...)` is bypassed by widget-level down/up flow.
  - `HandleMouseUp(... emitClick)` now falls back to the last stable hover hit when release-time hit test is transiently null, so links/controls still activate during pointer-target flicker.
  - `HandleMouseUp(... emitClick)` now also resolves activation targets through engine-layout fallback hit testing (`BrowserHost.HitTestElementAtViewportPoint(...)`) when paint-tree hit results do not carry `NativeElement`, restoring click/focus reliability for controls.
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
Startup and UI-driven navigation now canonicalize host-only internal URLs before dispatch so reload/history/UI paths that surface `fen://newtab/` or `fen://settings/` do not bypass the intended internal handlers.

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

#### SettingsPageWidget.cs (Lines 1-1350)

The browser's internal settings page (`fen://settings`).

- **Lines 111-469**: **`InitializeControls`**: Builds the massive UI tree for settings.
- **Lines 746-1138**: **`Paint`**: Custom rendering logic for the settings interface.

#### TabBarWidget.cs (Lines 1-388) & TabWidget.cs (Lines 1-250)

The visual implementation of the browser tabs.

- **TabWidget**: Renders the individual tab shape (trapezoid/rounded), title, and close button.
- **TabBarWidget**: Manages the collection of tabs, scrolling, and dragging logic.

#### ToolbarWidget.cs (Lines 1-168)

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

#### StackPanel.cs (Lines 1-72) & DockPanel.cs (Lines 1-120)

Layout containers for arranging child widgets.

#### InspectorPopupWidget.cs (Lines 1-185)

The container window for the undocked DevTools.

#### SiteInfoPopupWidget.cs (Lines 1-244)

The "Lock Icon" popup showing SSL certificate details and cookies.

#### `SwitchWidget.cs` (Lines 1-100)

Toggle switch UI (used in Settings).

#### `TextInputWidget.cs` (Lines 1-400)

Base class for text entry fields (cursor rendering, selection handling).

#### AddressBarWidget.cs (Lines 1-672)

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
  - Remote debug startup is now opt-in (`FEN_REMOTE_DEBUG=1`) with configurable port/bind values and required session-token auth.
  - `FEN_REMOTE_DEBUG_TOKEN` remains supported for explicit secrets; when omitted, `RemoteDebugServer` now mints an ephemeral per-launch token and requires it on both JSON discovery and WebSocket upgrade requests.
  - WebDriver startup is now opt-in (`FEN_WEBDRIVER=1`) with optional `FEN_WEBDRIVER_PORT`.
  - Host-to-WebDriver driver wiring now uses direct `WebDriverServer.SetDriver(...)` instead of reflection over private fields.

### 6.6 Phase-1 CLI Normalization (2026-02-18)

- `Program.cs`
  - Removed the duplicate `--wpt` command branch to keep a single authoritative WPT entrypoint.
  - Reduced command-routing ambiguity in CLI startup flow by consolidating WPT handling into one path.

### 6.7 Phase-2 Default Path Hygiene (2026-02-18)

- `Program.cs`
  - Removed machine-specific absolute default paths for test suites.

### 6.8 Root Diagnostic Cleanup Script Hardening (2026-03-21)

- `clean_root.ps1`
  - 2026-04-18 update: canonical diagnostics now live under workspace `logs/` only; the earlier root-level artifact cleanup behavior is legacy context.
  - The script removes the generated text/log/screenshot artifacts used by FenBrowser debugging from `logs/`, including `debug_screenshot.png`, `dom_dump.txt`, `debug_log.txt`, `layout_engine_debug.txt`, `js_debug.log`, CSS debug dumps, xcom stdout/stderr captures, and captured JavaScript payload dumps such as `main.*.js` / `vendor*.js`.
  - Cleanup remains bounded to diagnostic-style artifacts so repository documentation and source files are not touched.
  - Default Test262 root now resolves to `Path.Combine(Directory.GetCurrentDirectory(), "test262")`.
  - Default WPT root now resolves to `Path.Combine(Directory.GetCurrentDirectory(), "wpt")`.

- 2026-04-18 host/tooling path alignment:
  - snapshot-style host tests now resolve diagnostic HTML from the canonical workspace `logs/` directory only;
  - `FenBrowser.Tooling` reads `layout_engine_debug.txt` and emits `css_debug.txt` through `DiagnosticPaths`, removing current-directory/root assumptions during diagnostics workflows.

### 6.8 Phase-5 Host Integration Completion (2026-02-18)

- `WebDriver/FenBrowserDriver.cs`
- `WebDriver/HostBrowserDriver.cs`
  - Expanded host WebDriver adapters to implement the full command surface used by `FenBrowser.WebDriver` (window context, element-state, cookies, actions, alerts, print, and element screenshot operations).
  - Driver-side behavior now routes through `BrowserHost` APIs instead of placeholder-only paths.

- `Widgets/SettingsPageWidget.cs`
  - Privacy settings UI keeps runtime-wired controls visible (`Safe Browsing`, `Block pop-ups`).
  - `ImproveBrowser` remains hidden in privacy UI until broader UX policy for telemetry controls is finalized.

### 6.9 Render/Perf P1 Host Closure (2026-03-30)

- `BrowserIntegration` now routes animation pulses, host repaint requests, and render commits through explicit frame-request semantics instead of opportunistic raw renderer calls.
- Host frame telemetry now mirrors renderer commit truth into structured logs, including:
  - invalidation reason
  - request source
  - damage region count
  - damage area ratio
  - raster mode
  - watchdog outcome
- The host-side Google closure run proved the new steady-state contract:
  - first navigation commit remained heavy at `557.24ms`
  - later converged animation frames settled to `2.64ms` to `3.15ms`
  - those tail frames carried `usedDamageRasterization=true` and `layoutUpdated=false`
- `BrowserHost` rendered-text capture now includes visible control fallback text such as placeholders and accessible labels when those labels are actually the visible affordance, while avoiding duplicate aria-label fallback when visible text already exists.
- Image-loader repaint and relayout callbacks now align with active-DOM semantics, and burst image prewarm no longer causes one host relayout event per decoded asset.

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

### 6.20 Host Focus Bridge Hardening (2026-03-07)

- `BrowserIntegration.cs`
  - `FocusNode(...)` no longer acts as a visual-only placeholder.
  - Programmatic host focus now mirrors into the engine-visible focus state by updating `document.activeElement`, `ElementStateManager`, and repaint wake signaling in the same path.

- Net effect:
  - Host-driven focus requests from JS/automation stay aligned with engine selector state and WebDriver active-element queries instead of producing highlight-only behavior.

### 6.21 WebDriver Window Handle Convergence (2026-03-07)

- `WebDriver/HostBrowserDriver.cs`
  - `NewWindowAsync(...)` now returns the real newly active tab handle (`BrowserTab.Id`) instead of a fabricated GUID, preserving switchability across WebDriver window commands.
  - Added real `GetWindowHandleAsync()`, `GetWindowHandlesAsync()`, and `CloseWindowAsync()` host-tab adapters so WebDriver window enumeration and close semantics reflect actual `TabManager` state.

- `WebDriver/FenBrowserDriver.cs`
  - `NewWindowAsync(...)` now mirrors host tab identity rather than inventing synthetic handles.
  - `SwitchToWindowAsync(...)` no longer no-ops; it now resolves WebDriver window handles back to real host tabs through `TabManager`.
  - Added direct current-window, window-list, and close wiring against `TabManager`, removing the remaining fabricated/no-op window-context behavior in the in-process adapter.

- Net effect:
  - Newly created WebDriver windows/tabs now return handles that can actually be switched back to.
  - WebDriver current-window queries, window-handle enumeration, and close operations now track real host tabs instead of session-only synthetic state.

### 6.22 Brokered Renderer Factory Hardening (2026-03-07)

- `ProcessIsolation/ProcessIsolationCoordinatorFactory.cs`
  - Removed the stale “blank UI / incomplete skeleton” rationale from brokered-mode selection.
  - Added `FEN_PROCESS_ISOLATION=auto` as an explicit brokered-renderer selection alias alongside `brokered`.
  - Default in-process mode messaging now reflects the real state: in-process remains the default, but brokered mode is an available out-of-process renderer path rather than a known-black-screen trap.

- Net effect:
  - Host startup no longer documents brokered renderer mode as unusable when shared-memory frame return is already wired.



### 6.20 Host Runtime Async Evaluation Bridge (2026-03-06)

- `BrowserIntegration.cs`
  - Replaced the remaining synchronous DevTools script-evaluation bridge (`Task.WhenAny(...).GetResult()`) with `EvaluateScriptAsync(...)`.
  - Host evaluation now awaits engine execution asynchronously and applies a bounded `WaitAsync(TimeSpan.FromSeconds(2))` timeout without blocking the host thread.
- Net effect:
  - Removes a correctness-critical blocking wait from the host runtime path.
  - Keeps DevTools evaluation time-bounded while preserving async flow across the host/engine boundary.



### 6.21 Brokered Renderer Child Loop Async Hardening (2026-03-06)

- `Program.cs`
  - Converted the `--renderer-child` IPC loop to async message handling.
  - Removed synchronous `ReadLineAsync().Wait(500)` / `.Result` usage from the child pipe loop.
  - Removed `.GetAwaiter().GetResult()` navigation and key-input execution from renderer-child message handling in favor of awaited async calls.
- `ProcessIsolation/RendererChildLoopIo.cs`
  - Added a dedicated timed line-read helper so pipe polling remains bounded without blocking the child loop thread.
- Net effect:
  - Keeps brokered renderer-child IPC polling time-bounded while avoiding blocking waits in the child command loop.
  - Preserves parent-liveness checks and handshake semantics without sync-over-async bridges in the active IPC path.


### 6.22 Host DevTools DOM/CSS Dispatcher Hardening (2026-03-06)

- `ChromeManager.cs`
  - Removed the remaining synchronous DevTools UI-thread bridge (`RunOnMainThread(...).GetAwaiter().GetResult()`) from DOM/CSS setup.
  - DOM inspection, highlight, CSS reads, inline style edits, and repaint requests now flow through async UI-thread dispatch.
- Net effect:
  - DevTools DOM/CSS access no longer blocks the host thread with sync-over-async waits.
  - Host thread ownership is preserved for DOM/CSS mutation and inspection paths used by DevTools.

### 6.23 Host Async Entry Dispatch (2026-03-06)

- `Program.cs`
  - Converted host entrypoint to `async Main` so renderer-child startup and `--test262` CLI execution no longer use `.GetAwaiter().GetResult()` bridges.
  - Added `ResolveStartupMode(...)` to centralize startup dispatch precedence across renderer-child, Test262, WPT, Acid2, WebDriver, and normal browser modes.
- Net effect:
  - Removes the remaining sync-over-async waits from the active host entry dispatch path.
  - Startup mode precedence is now explicit and regression-testable instead of being spread across inline argument checks.


### 6.24 Clipboard Retry Backoff Hardening (2026-03-06)

- `Widgets/ClipboardHelper.cs`
  - Replaced fixed `Thread.Sleep(10)` retry loops with a shared `TryOpenClipboardWithRetry(...)` helper.
  - Clipboard open retries now use bounded progressive `SpinWait` backoff instead of coarse fixed sleeps on the host thread.
  - `GetText()` and `SetText()` now share the same retry policy while preserving the existing synchronous widget-facing API.
- Net effect:
  - Removes the last direct sleep-based polling path from `FenBrowser.Host`.
  - Keeps clipboard contention handling bounded without imposing fixed 10ms stalls on every retry.

### 6.25 Remote DevTools Session-Token Hardening (2026-03-07)

- `ChromeManager.cs`
- `FenBrowser.DevTools/Core/RemoteDebugServer.cs`
  - Remote DevTools remains opt-in (`FEN_REMOTE_DEBUG=1`), but it no longer permits unauthenticated operation when enabled.
  - If `FEN_REMOTE_DEBUG_TOKEN` is not supplied, the remote debug server now generates an ephemeral per-launch token and logs it once for operator discovery.
  - JSON discovery endpoints and advertised WebSocket targets now carry tokenized debugger URLs, and unauthorized requests receive `401 Unauthorized` with a bearer challenge.
- Net effect:
  - Enabling the remote debug port no longer exposes arbitrary CDP evaluation to any local or external client that can reach the socket.
  - Token enforcement now applies uniformly to both target discovery and WebSocket command ingress.

### 6.26 Renderer Sandbox Fail-Closed Hardening (2026-03-07)

- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Brokered renderer launch no longer silently falls back to an unsandboxed child when sandbox creation or custom sandbox spawn fails.
  - Unsandboxed renderer fallback is now fail-closed by default and requires explicit operator override via `FEN_RENDERER_ALLOW_UNSANDBOXED=1`.
  - Sandbox attach failures after child launch now also terminate the child instead of leaving a live unsandboxed renderer behind under the normal brokered path.
- Net effect:
  - Brokered process isolation no longer degrades into an implicit unsandboxed renderer launch on sandbox setup failure.
  - Any unsandboxed renderer launch now requires an explicit environment opt-in instead of happening as an error-handling side effect.

### 6.27 WebDriver Browser-Origin Rejection Hardening (2026-03-07)

- `FenBrowser.WebDriver/Security/OriginValidator.cs`
  - WebDriver now rejects all browser `Origin` headers by default, even from loopback pages.
  - Browser-origin access now requires explicit operator opt-in via `FEN_WEBDRIVER_ALLOW_BROWSER_ORIGINS=1`; non-browser clients without an `Origin` header continue to work over loopback.
- Net effect:
  - Loopback binding is no longer the only CSRF control for WebDriver.
  - Local webpages can no longer drive the automation endpoint by default just by originating from `http://localhost`.

### 6.28 WebDriver Browser Preflight Narrowing (2026-03-07)

- `FenBrowser.WebDriver/WebDriverServer.cs`
  - Browser-origin `OPTIONS` preflight is no longer accepted unconditionally once the origin gate passes.
  - WebDriver now validates `Access-Control-Request-Method` against the actual command surface (`GET`, `POST`, `DELETE`, `OPTIONS`) and rejects preflights that request unsupported methods.
  - Requested headers are now narrowed to the currently supported browser-facing set (`Content-Type`) instead of being treated as implicitly accepted.
- Net effect:
  - Browser-origin mode is tighter when explicitly enabled: CORS preflight now reflects the real automation ingress contract instead of returning a broad unconditional `204`.
  - Unexpected browser preflight shapes fail closed before any command routing occurs.

_End of Volume IV_
### 6.28 Renderer Startup-Contract Hardening (2026-03-07)

- `ProcessIsolation/RendererIpc.cs`
  - Added an explicit renderer-child readiness contract via `WaitForReadyAsync(...)`.
  - IPC connection failure, early child exit, and read-loop termination now all complete the readiness gate negatively instead of leaving the host with an indeterminate child state.
- `ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
  - Brokered renderer startup now waits for a bounded `Ready` handshake before marking the session active.
  - A child that starts at the OS level but never completes IPC startup is now killed and rejected instead of being treated as a valid live renderer.
  - Brokered launch now rejects `NullSandbox` resolution and unsupported sandbox factories for renderer children unless the explicit override `FEN_RENDERER_ALLOW_UNSANDBOXED=1` is present.
- Net effect:
  - Brokered renderer mode no longer treats `Process.Start(...)` alone as a successful target-process launch contract.
  - Child renderer startup is now fail-closed both on sandbox resolution and on IPC-ready handshake completion.

### 6.29 Network Target-Process Startup Contract (2026-03-07)

- `ProcessIsolation/Network/NetworkProcessIpc.cs`
  - Added a real ready-handshake contract for the broker-side network session via `WaitForReadyAsync(...)`.
  - Early child exit, IPC connect failure, and read-loop failure now all complete the startup gate negatively instead of leaving the host with an indeterminate network child state.
- `ProcessIsolation/Network/NetworkChildProcessHost.cs` (new)
  - Added a broker-side launcher for the dedicated network target process.
  - Launch now rejects unsupported sandbox factories and `NullSandbox` resolution by default, with explicit override only through `FEN_NETWORK_ALLOW_UNSANDBOXED=1`.
  - A started child is not accepted until it completes the bounded `Ready` handshake.
- `Program.cs`
  - Added `StartupMode.NetworkChild` and `--network-child` / `FEN_NETWORK_CHILD=1` boot path.
  - Added a real network child command loop that authenticates the broker handshake, executes outbound HTTP requests, streams response head/body messages over IPC, supports cancellation, and exits when the parent dies or shutdown is requested.
- Net effect:
  - Fen now has an actual bootable network target-process mode instead of only a network IPC schema.
  - Network child startup is fail-closed on both sandbox resolution and ready-handshake completion.
### 6.30 Label click activation parity (2026-03-07)

- Host click handling in `BrowserApi` now mirrors DOM wrapper label activation behavior:
  - clicking a `<label>` resolves its associated control through `for=<id>` or first labelable descendant
  - forwarded activation is skipped for disabled form controls
- This keeps rendered pointer clicks aligned with the newer DOM-side label activation path instead of letting host clicks and script-driven clicks diverge.
### 6.31 GPU and Utility Target-Process Startup Contracts (2026-03-07)
- `FenBrowser.Host/ProcessIsolation/Targets/TargetProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Targets/TargetChildProcessHost.cs`
- `FenBrowser.Host/Program.cs`
- Added real broker/child startup contracts for `--gpu-child` and `--utility-child` instead of leaving those Milestone A target processes as roadmap-only entries.
- Broker-side launch now exists through `TargetChildProcessHost`, which:
  - applies `OsSandboxProfile.GpuProcess` or `OsSandboxProfile.UtilityProcess`
  - rejects unsupported sandbox factories by default
  - rejects `NullSandbox` by default unless the corresponding explicit override env var is set
  - requires a bounded ready handshake before considering the child live
- Host entrypoint now supports `StartupMode.GpuChild` and `StartupMode.UtilityChild`, and both child loops validate startup policy assertions (`FEN_TARGET_KIND`, sandbox profile, auth token, parent liveness) before replying with `Ready`.
- This is a startup-contract tranche, not full Milestone A closure: compositor/GPU work submission and utility-task routing still need to be moved onto these target processes before the architecture can be marked production-complete.
### 6.32 IPC fuzz-baseline harness (2026-03-07)
- `FenBrowser.Host/ProcessIsolation/IpcFuzzBaseline.cs`
- Added a reusable IPC fuzz-baseline harness for the host process-isolation channels instead of leaving Milestone `A3` as documentation-only.
- The baseline currently mutates serialized envelopes for:
  - renderer IPC
  - network IPC
  - generic target-process IPC
- Each suite exercises malformed JSON, truncated payloads, repeated envelopes, type-shape mismatches, oversized field substitutions, and byte-level mutations, and treats any thrown exception during parse/deserialization as a fuzz failure.
- This is a parser/baseline tranche, not full end-to-end fault-injection coverage across live pipe sessions.
### 6.33 Auxiliary target-process runtime ownership (2026-03-07)
- `FenBrowser.Host/ProcessIsolation/ProcessIsolationRuntime.cs`
- `ProcessIsolationRuntime` now broker-owns auxiliary target-process launch/disposal instead of leaving the new network/GPU/utility hosts as orphaned helper classes.
- When an out-of-process renderer coordinator is installed, the runtime now auto-starts:
  - network target process
  - GPU target process
  - utility target process
  unless `FEN_AUTO_START_TARGET_PROCESSES=0` disables that behavior.
- The runtime also now exposes the current auxiliary sessions for broker-side routing.
- This is a concrete Milestone `A2` process-migration tranche, but not full feature migration: network, GPU, and utility work still need more host subsystems moved onto those channels.

### 6.34 Host Repaint Thread-Ownership Hardening (2026-03-09)

- `FenBrowser.Host/Widgets/Widget.cs`
  - Widget invalidation is now main-thread-owned.
  - Off-thread `Invalidate(...)` calls no longer mutate `DirtyRect` directly; they are coalesced and marshaled onto the host main thread before bubbling through the widget tree.
- `FenBrowser.Host/WindowManager.cs`
  - Added explicit host thread-state surface (`IsMainThreadInitialized`, `IsOnMainThread`) so UI-facing code can cheaply guard thread ownership.
- `FenBrowser.Host/ChromeManager.cs`
  - Active-tab URL, title, and loading-state callbacks now marshal onto the UI thread before touching `ToolbarWidget`, `StatusBarWidget`, the root invalidation path, or the native window title.
- `FenBrowser.Tests/Host/WidgetInvalidationDispatchTests.cs`
  - Added a regression proving that background-thread invalidation queues work for the UI thread instead of mutating widget dirty state inline.
- Net effect:
  - Host repaint progress no longer depends on incidental mouse-move invalidations to wake visible UI state after engine/background callbacks.
  - Dirty-region ownership is now consistent with the Host threading model, reducing intermittent “blank until hover/mouse move” behavior on Google and similar pages.

### 6.35 Host Committed-Frame Preservation During Progressive Parse/Re-cascade (2026-03-09)

- `FenBrowser.Host/BrowserIntegration.cs`
  - Host frame recording now preserves the last committed `SKPicture` whenever the engine has temporarily cleared computed styles during progressive parse checkpoints or dynamic re-cascade.
  - If a committed frame already exists and the host only has a DOM snapshot without computed styles, `RecordFrame(...)` now defers the swap instead of replacing the visible frame with a transient structural/unstyled frame.
- `FenBrowser.Tests/Host/BrowserIntegrationFrameStabilityTests.cs`
  - Added a regression proving that a committed frame is retained when styles are temporarily unavailable.
- Net effect:
  - Complex pages like Google no longer flicker by swapping a good frame out for a styleless checkpoint frame and then swapping back once CSS catches up.
  - Visible content remains stable across parse-progress and recascade churn, reducing the “disappears and reappears” symptom without blocking later styled frame commits.

### 6.36 Host Repaint Pulse Hardening After Navigation (2026-03-21)

- `FenBrowser.Host/BrowserIntegration.cs`
  - `RecordFrame(...)` now emits a single `NeedsRepaint` signal after a fresh frame is committed instead of scheduling delayed backup invalidations at `80ms` and `250ms`.
  - `StartPostNavigationRepaintPulse()` still wakes the render thread during early navigation, but it no longer invalidates the Compositor directly and it stops once the first styled frame has been committed.
- Net effect:
  - The Host no longer asks the UI layer to redraw from speculative or stale post-navigation pulses after a committed frame already exists.
  - Google-style page-wide flicker is reduced because visible swaps now follow committed renderer output instead of redundant host-side repaint nudges.

### 6.37 Host Entry-Point Closure And Auxiliary Target Contracts (2026-03-29)

- `FenBrowser.Host/Program.cs`
  - The host entrypoint now resolves only the real runtime modes:
    - `Browser`
    - `RendererChild`
    - `NetworkChild`
    - `GpuChild`
    - `UtilityChild`
  - Tooling arguments such as `--test262`, `--wpt`, `--acid2`, `--port=...`, and `--debug-css` are now rejected with an explicit error directing operators to `FenBrowser.Tooling`.
- `FenBrowser.Host/BrowserIntegrationRuntime.cs`
  - Added a runtime-specific integration bootstrap surface so browser integration startup does not need to share ownership with former tooling entrypoint code.
- `FenBrowser.Host/ProcessIsolation/Targets/TargetProcessContract.cs`
- `FenBrowser.Host/ProcessIsolation/Gpu/GpuProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Utility/UtilityProcessIpc.cs`
  - GPU and utility target processes are no longer empty placeholder seams; both now expose explicit profile/capability contracts that broker and child sides validate during startup.
- `FenBrowser.Host/ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
- `FenBrowser.Host/ProcessIsolation/Network/NetworkChildProcessHost.cs`
- `FenBrowser.Host/ProcessIsolation/Targets/TargetChildProcessHost.cs`
  - Child-process launch now flows through `SandboxLaunchPolicy`, so renderer/network/GPU/utility children fail closed on unsupported or `NullSandbox` resolution unless the corresponding override is explicitly set.
  - Structured process-isolation logging scopes now attach target kind, sandbox posture, and correlation metadata to launch/ready/failure paths.
- Net effect:
  - Host startup no longer owns conformance or automation runner modes.
  - Auxiliary process seams are now declared runtime contracts instead of placeholder files.
  - Unsandboxed fallback is explicit policy, not silent drift.
- Verification:
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -nologo`
  - `dotnet build FenBrowser.sln -nologo`
  - both completed successfully on `2026-03-29`.

### 6.38 Host Packaging Determinism And Manifest Hygiene (2026-03-30)

- `FenBrowser.Host/FenBrowser.Host.csproj`
  - The host project now declares explicit assembly/product metadata (`AssemblyName`, `Company`, `Product`, `Authors`) instead of leaving packaging identity partially implicit.
  - Build output is now deterministic by default with portable PDBs and CI-aware deterministic mode, which reduces machine-specific binary drift and keeps crash/debug symbol expectations stable.
- `FenBrowser.Host/app.manifest`
  - The Windows manifest now declares `longPathAware=true` and an explicit supported Windows compatibility target.
  - Existing `asInvoker` privilege posture and Per-Monitor-V2 DPI declarations remain intact; the change here is that Fen's packaging posture is now explicit rather than relying on ambient OS defaults.
- Why this mattered:
  - Host packaging is part of the production surface. If manifest/build metadata drifts by machine or toolchain, reproducibility and operator expectations drift with it.
  - P2 requires these surfaces to be deliberate even when the files themselves stay small.
- Verification:
  - `dotnet build FenBrowser.sln -c Debug -v minimal`: pass on `2026-03-30`.
  - required clean-state host run on `2026-03-30` completed and emitted the expected workspace-root diagnostics set, including a painted `debug_screenshot.png`.

### 6.39 Host Input And Context-Menu Thin-Contract Hardening (2026-03-30)

- `FenBrowser.Host/ProcessIsolation/RendererInputEvent.cs`
- `FenBrowser.Host/ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
- `FenBrowser.Host/ProcessIsolation/RendererIpc.cs`
- `FenBrowser.Host/Program.cs`
  - `RendererInputEvent` now normalizes non-finite coordinates and trims keyboard/text payloads at the contract boundary.
  - Brokered IPC send/receive paths now drop meaningless keyboard events rather than serializing empty payload churn into the renderer child.
  - Mouse-up click dispatch now routes through explicit `ShouldEmitClick` semantics instead of repeating ad hoc `EmitClick && Button == 0` checks.
- `FenBrowser.Host/Context/ContextMenuItem.cs`
- `FenBrowser.Host/Context/ContextMenuBuilder.cs`
- `FenBrowser.Host/Widgets/ContextMenuWidget.cs`
  - Context-menu items now normalize labels and shortcut text, expose safe invocation state, and separators are explicitly non-invokable.
  - Builder-generated commands are now disabled when the corresponding handler is absent instead of presenting enabled no-op entries to the user.
  - The widget activation path now uses the item-level invocation contract instead of reaching directly into raw delegates.
- Why this mattered:
  - Host interaction surfaces are user-facing contracts. Thin files here still need deterministic normalization and truthful enablement state.
  - A production browser should not serialize NaN input coordinates across a process boundary or show enabled context-menu items that cannot actually execute.
- Verification:
  - `FenBrowser.Tests/Host/HostThinContractTests.cs`
  - focused regression slice on `2026-03-30`: pass.

### 6.40 Host Widget Debug-Trace Routing Unification (2026-03-30)

- `FenBrowser.Host/Widgets/WebContentWidget.cs`
- `FenBrowser.Host/Widgets/SwitchWidget.cs`
- `FenBrowser.Host/Widgets/BookmarksBarWidget.cs`
  - The host's ad hoc click-debug traces now route through `DiagnosticPaths.GetLogArtifactPath("click_debug.log")` instead of writing to a mix of `bin/Debug/net8.0/logs` and LocalAppData.
  - This keeps widget-level evidence in the same workspace-root diagnostics tree as the structured logs, raw source, engine dumps, and rendered text snapshots.
- Why this mattered:
  - When the host shell misroutes even "small" debug traces, production incident review turns into directory archaeology.
  - P2 required the remaining thin host traces to obey the same diagnostics contract as the rest of the browser.
- Verification:
  - required clean-state host run on `2026-03-30` emitted `logs/click_debug.log` under the workspace root, and the host Debug log directory remained empty after the run.

### 6.41 Host Render Request Ownership And Committed-Frame Telemetry (2026-03-30)

- `FenBrowser.Host/BrowserIntegration.cs`
  - Host frame recording now routes through the formal renderer contract (`RenderFrameRequest`) instead of calling the renderer's raw paint path directly.
  - `BrowserIntegration` now owns a pending invalidation-reason/source accumulator so frame requests retain why the frame was asked for:
    - navigation
    - viewport
    - DOM/style mutation
    - animation/timer
    - diagnostics
    - interaction

### 6.42 Host Dom-Poller Settling Guard (2026-04-11)

- `FenBrowser.Host/BrowserIntegration.cs`
  - The host DOM poller no longer keeps re-adopting late root/style snapshot churn once a styled frame has already been committed for the current navigation.
  - `_hasFirstStyledRender` is now promoted at the successful frame-commit point instead of relying only on `root.ComputedStyle`, so the host can reliably recognize that a page has reached its first styled steady state.
  - The poller remains available as a bootstrap/recovery path when root/style state is genuinely missing, but it stands down for settled documents instead of forcing additional `Dom, Style, Diagnostics` relayouts.
- Why this mattered:
  - On `https://example.com/`, the old poller path was still issuing late `BrowserIntegration.DomPoller` full frames long after first paint. That caused the page to visibly jump to a different layout position several seconds after load, which users could mistake for a mouse-hover repaint bug.
  - A settled static document must not relayout simply because the host observed a new snapshot object reference.
- Verification:
  - Clean-state host repro on `2026-04-11` with `dotnet run --project FenBrowser.Host -- https://example.com/` and a 32-second wait no longer emitted late `BrowserIntegration.DomPoller` frame commits in the fresh `logs/fenbrowser_*.jsonl`.
  - Fresh `debug_screenshot.png` stayed visually stable for the `example.com` centered content layout after first paint.
  - Follow-up host-side hover hardening in `FenBrowser.FenEngine/Rendering/BrowserApi.cs` now ignores passive `html` / `body` / non-actionable container hover targets when normalizing mousemove hover state, so stationary pointer drift over centered content does not keep re-triggering full recascade/repaint churn on static pages like `example.com`.
  - `RecordFrame(...)` now seeds the last committed frame into the renderer when `BaseFrameReusePolicy` says reuse is safe, which turns reusable-frame behavior into a real steady-state production path.
  - The committed page frame is now kept separate from transient overlays/highlights:
    - the page frame stays reusable
    - overlays are drawn in the host presentation path
    - screenshot capture now composites through the same presentation path so operator evidence still matches what the user sees
  - Host-side frame commits now emit structured `[FRAME] Commit` entries containing:
    - requested-by source
    - invalidation reason
    - raster mode
    - base-frame seeded flag
    - layout/paint/raster timings
    - DOM/box/paint counts
    - watchdog state

- `FenBrowser.Host/ProcessIsolation/RendererIpc.cs`
- `FenBrowser.Host/Program.cs`
  - Renderer child frame requests now use the same render-frame contract and return the same telemetry surface to the host.
  - Frame-ready payloads now include requested-by source, invalidation reason, raster mode, staged timing data, and layout/paint-tree state so brokered paths stop being opaque relative to the in-process path.

- Net effect:
  - The host and renderer now agree on why a frame was produced.
  - Reusable committed frames are no longer nominal state that only exists inside the renderer.
  - The live host path can distinguish expensive first/full commits from cheap preserved-base-frame steady-state commits in operator-visible logs.

- Verification:
  - `dotnet build FenBrowser.sln -c Debug -v minimal -nologo`: pass on `2026-03-30`.
  - `dotnet test FenBrowser.Tests\FenBrowser.Tests.csproj -c Debug -v minimal -nologo --no-build --filter "FullyQualifiedName~RenderFrameTelemetryTests|FullyQualifiedName~RenderWatchdogTests|FullyQualifiedName~LayoutConstraintResolverTests"`: pass (`7/7`) on `2026-03-30`.
  - required clean-state host run on `2026-03-30` emitted:
    - `debug_screenshot.png`
    - `dom_dump.txt`
    - `logs/raw_source_20260330_123307.html`
    - `logs/fenbrowser_20260330_123306.log`
    - `logs/fenbrowser_20260330_123306.jsonl`
  - Runtime outcome:
    - the first Google commit was still expensive and fully rastered.
    - subsequent animation-driven commits were logged as `PreservedBaseFrame` with the same committed frame reused, and later steady-state frames dropped to near-zero total cost.

### 6.42 Host Render/Perf P2 Deadline-Aware Scheduling And Frame-Telemetry Expansion (2026-03-30)

- `FenBrowser.Host/BrowserIntegration.cs`
  - The host render loop now carries per-frame event-loop slice telemetry:
    - processed task count
    - interactive/user-visible/background task counts
    - deferred background task count
    - pending queue counts by priority bucket
    - reserved render budget for the current frame
  - Busy frames now prioritize interactive work and defer background work instead of draining the queue indiscriminately under repaint pressure.
  - Low-signal invalidations (`Timer`, `Animation`, `Scroll`, `Input`, `Overlay`) no longer automatically trigger a full verification report, which keeps runtime verification closer to meaningful content transitions.
  - Structured `[FRAME] Commit` entries now include:
    - event-loop slice data
    - image-cache counts/bytes/hits/misses/evictions
    - font-cache counts/bytes/hits/misses/evictions
    - text-measure cache counts/bytes/hits/misses/evictions
- Why this mattered:
  - P2 closure required the host to stop behaving like a passive frame presenter.
  - A production browser needs deadline-aware task shaping at the host/render boundary and first-class operator telemetry for why a frame was cheap or expensive.
- Verification:
  - required clean-state host run on `2026-03-30` emitted:
    - `debug_screenshot.png`
    - `dom_dump.txt`
    - `logs/raw_source_20260330_153456.html`
    - `logs/engine_source_20260330_153457.html`
    - `logs/rendered_text_20260330_153457.txt`
    - `logs/fenbrowser_20260330_153454.log`
    - `logs/fenbrowser_20260330_153454.jsonl`
  - the structured frame stream now records `eventLoop*`, `imageCache*`, `fontCache*`, and `textMeasure*` fields on each commit.

### 6.43 Host New-Tab DevTools Lifetime Fix (2026-04-02)

- `FenBrowser.Host/ChromeManager.cs`
  - Active-tab DevTools setup still routes through a shared `DevToolsServer`, but that server reset is now safe for repeated tab creation.
  - The host no longer crashes when the new-tab button or `Ctrl+T` switches inspection to a freshly created tab.

- `FenBrowser.Host/DevToolsHostAdapter.cs`

### 6.44 Process-Isolation Log Batch Aggregation (2026-04-20)

- `FenBrowser.Host/Program.cs`
  - Renderer, network, and target child modes now forward structured engine logs to the parent over IPC using `LogBatch` envelopes.
  - Child forwarding is queued and flushed in bounded batches to avoid blocking frame/fetch hot paths.
- `FenBrowser.Host/ProcessIsolation/EngineLogIpc.cs` (new)
- `FenBrowser.Host/ProcessIsolation/ProcessIsolationLogCollector.cs` (new)
  - Added host-side collector that normalizes child context (`source`, `tabId`) and republishes events into `EngineLog`.
- `FenBrowser.Host/ProcessIsolation/RendererIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Network/NetworkProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Targets/TargetProcessIpc.cs`
  - Added `LogBatch` message handling so broker-side sessions ingest child process logs as first-class structured events.
- Net effect:
  - Brokered process logs are no longer fragmented by process boundary.
  - Parent NDJSON/trace output and downstream diagnostics now include child renderer/network/target events with correlation metadata.

### 6.45 Host Process-Isolation Logging Migration to EngineLog (2026-04-20)

- `FenBrowser.Host/Program.cs`
- `FenBrowser.Host/ProcessIsolation/RendererIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Network/NetworkProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Targets/TargetProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/BrokeredProcessIsolationCoordinator.cs`
- `FenBrowser.Host/Widgets/SettingsPageWidget.cs`
  - Replaced legacy `FenLogger` writes in host process-isolation execution paths with direct `EngineLog` subsystem/severity writes.
  - Replaced child-mode `LogManager.InitializeFromSettings()` bootstrap with `EngineLog.InitializeFromSettings()`.
  - Replaced host settings-page runtime logging bootstrap refresh with `EngineLog.InitializeFromSettings()` so settings toggles now refresh the new runtime directly.
- Net effect:
  - Critical broker/child lifecycle diagnostics now originate directly from the new engine logging runtime instead of legacy wrappers.
  - Existing host-adapter disposal remains the handoff boundary for repaint, console, network, and JSON relay detachment before a new tab is attached.
  - The fix depended on honoring that disposal boundary and pairing it with a real server reset, rather than stacking a second protocol-domain registration on the previous tab session.

- Runtime proof:
  - Clean host verification on `2026-04-02` started on Google, then navigated the active tab to `fen://newtab` without a fatal shutdown.
  - New-tab diagnostics were emitted under:
    - `logs/raw_source_20260402_120945.html`
    - `logs/engine_source_20260402_120945.html`
    - `logs/rendered_text_20260402_120945.txt`
    - `logs/fenbrowser_20260402_120934.log`
    - `logs/fenbrowser_20260402_120934.jsonl`

### 6.44 Host Theme Propagation Into Browser Surface Media Features (2026-04-04)

- `FenBrowser.Host/Theme/ThemeManager.cs`
  - Host theme application now synchronizes the browser-wide theme preference into `BrowserSettings` only for deliberate theme changes, instead of overwriting startup `System` mode during static initialization.
  - Startup initialization now mirrors the stored browser preference into the host theme without collapsing `System` into an unconditional persisted `Light` or `Dark` write.

- Runtime effect:
  - the host theme and the browser surface now agree about `prefers-color-scheme`, which lets engine-side `matchMedia("(prefers-color-scheme: dark)")` and CSS media evaluation reflect the actual host-visible theme state.
  - This closes the earlier drift where the UI theme could change while the browser-surface media contract still reported the wrong color scheme to the page.

- Why this mattered:
  - browser-surface media parity is a host-to-engine contract, not just an engine concern.
  - Persisting the wrong theme at startup would have broken both user preference integrity and page-facing media-query correctness.

- Verification:
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug --no-restore`: pass on `2026-04-04`.
  - targeted browser-surface regression slice on `2026-04-04` kept `BrowserSettingsTests` and `JavaScriptEngineLifecycleTests.MatchMedia_TracksThemeAndViewportSurfaceChanges` green.

### 6.45 Active-Tab Focus Stabilization For `fen://newtab` (2026-04-18)

- `FenBrowser.Host/ChromeManager.cs`
  - `OnActiveTabChanged(...)` now clears stale drag/capture state on tab activation:
    - `_isDragging = false`
    - `InputManager.Instance.ReleaseCapture()`
  - When the activated tab URL is `fen://newtab*`, host now explicitly focuses the address bar and selects all text.
- Runtime intent:
  - Prevents second-tab URL-edit freeze/stall reports where input focus remained in stale widget state after tab transitions.
  - Makes new-tab address editing deterministic without requiring extra click retries.

### 6.46 Host Crash Snapshot Automation for EngineLog (2026-04-20)

- `FenBrowser.Host/Program.cs`
  - Added crash-path failure bundle export on:
    - `AppDomain.CurrentDomain.UnhandledException`
    - `TaskScheduler.UnobservedTaskException`
    - top-level fatal host exception catch path.
  - Crash handlers now emit marker-tagged structured events (`Invariant`/`EngineBug`) and best-effort bundle-export diagnostics.
- Runtime effect:
  - Crash incidents now automatically persist a `Results/<timestamp>/` bundle via `EngineLog.ExportFailureBundle(...)` without manual operator capture.

### 6.47 Strict Inbound IPC Envelope Validation (2026-04-21)

- `FenBrowser.Host/ProcessIsolation/RendererIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Network/NetworkProcessIpc.cs`
- `FenBrowser.Host/ProcessIsolation/Targets/TargetProcessIpc.cs`
- `FenBrowser.Host/Program.cs`
  - Added strict inbound envelope gates for renderer/network/target channels:
    - typed message enum validation
    - bounded identifier/token/payload sizes
    - timestamp sanity window checks
    - renderer tab-id binding on inbound messages
  - Child command loops now reject duplicate handshake attempts and reject token-bearing non-handshake messages.
  - Invalid envelopes are rejected with explicit reason logging and do not enter command dispatch.
- Runtime effect:
  - IPC channels now fail closed at envelope boundary and preserve deterministic startup/runtime behavior when malformed input is received.
  - Broker-side renderer/network/target sessions now also enforce per-channel inbound message allowlists, rejecting structurally valid but role-invalid message types.
