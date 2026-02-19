# FenBrowser Codex - Volume VI: Extensions & Verification

**State as of:** 2026-02-18
**Codex Version:** 1.0

## 1. Overview

This volume details the infrastructure used to extend the browser and verify its correctness. FenBrowser emphasizes **Spec Compliance** over ad-hoc features, relying heavily on standard test suites (WPT, Test262, Acid2).

## 2. WebDriver Implementation (`FenBrowser.WebDriver`)

FenBrowser includes a compliant W3C WebDriver server, allowing it to be controlled by automation tools like Selenium.

### 2.1 Architecture

- **Server**: `WebDriverServer.cs` listens for HTTP REST requests (e.g., `POST /session`, `GET /url`).
- **Routing**: `CommandRouter` dispatches requests to specific command handlers.
- **Bridge**: The server communicates with `FenBrowser.Host` via the `IBrowser` interface to control the UI and engine.

### 2.2 Capabilities

- Session Management (Create/Delete).
- Navigation (Go to URL, Back/Forward).
- Element Interaction (Find Element, Click, Send Keys).
- Script Execution (Execute Async/Sync).

---

## 3. Verification Ecosystem (`FenBrowser.Tests`, `FenBrowser.FenEngine.Testing`)

Ensuring correctness requires rigorous testing against industry standards.

### 3.1 Unit Tests (`FenBrowser.Tests`)

Standard xUnit tests covering internal components:

- **Core**: DOM node logic, Attribute parsing.
- **Engine**: CSS Parser correctness, Layout arithmetic.
- **Html5lib**: Tests the Tokenizer against the tricky edge cases of the HTML5 spec.

### 3.2 Compliance Runners (`FenBrowser.FenEngine.Testing`, `FenBrowser.Test262`)

Specialized runners built to execute standard web test suites against the engine.

- **Test262Runner**: Runs the official ECMA-262 (JavaScript) conformance suite.
- **WPTTestRunner**: Runs the Web Platform Tests (WPT) for DOM/CSS.
- **AcidTestRunner**: Specific runner for the Acid2 layout test, verifying standard rendering compliance.
- **Harness-Generated Test262 NUnit Suite** (`FenBrowser.Test262`):
  - Uses `Test262Harness` code generation to produce NUnit fixtures directly from the Test262 corpus.
  - Uses FenRuntime adapter hooks (`BuildTestExecutor`, `ExecuteTest`, `ShouldThrow`) to run generated cases against FenEngine.
  - Keeps regeneration workflow scripted via `FenBrowser.Test262/generate_test262.ps1`.

### 3.3 The Verification Loop

1.  **Code Changes** are made in `FenEngine`.
2.  **Unit Tests** verify the specific component.
3.  **WPT/Test262** runners verify that the change adheres to the spec and doesn't regress existing features.
4.  **Acid2** verifies visual integrity.

---

## 4. Comprehensive Source Encyclopedia

This section maps **every key file** in the Extensions and Verification subsystems.

### 4.1 WebDriver Subsystem (`FenBrowser.WebDriver`)

#### `WebDriverServer.cs` (Lines 1-232)

The W3C-compliant HTTP Server implementation.

- **Lines 108-180**: **`HandleRequestAsync`**: The central request dispatcher, routing HTTP methods/paths to specific Command handlers.
- **Lines 182-193**: **`SendResponseAsync`**: Standardizes JSON responses according to the WebDriver wire protocol.

#### `CommandRouter.cs` (Lines 1-200+)

Routes URL patterns to `ICommand` implementations.

#### `SessionManager.cs` (Lines 1-150+)

Manages active browsing sessions (creation, deletion, timeouts).

### 4.2 Compliance Verification (`FenBrowser.FenEngine.Testing`, `FenBrowser.Test262`)

#### `Test262Runner.cs`

The ECMA-262 (JavaScript) conformance test runner.

- **Lines 92-213**: **`RunSingleTestAsync`**: Orchestrates a single test case: Parsing YAML metadata, executing JS, and validating results against expected outcomes.

#### `FenBrowser.Test262/Generated/Tests262Harness.Test262Test.generated.cs`

Generated NUnit base fixture from `Test262Harness`.

- **`RunTestCode`** now enforces correct negative-test semantics:
  - Expected-throw cases fail when no throw occurs.
  - Unexpected throws fail non-negative tests immediately.

#### `FenBrowser.Test262/Test262RuntimeAdapter.cs`

FenRuntime bridge for generated Test262 fixtures.

- **`State` partial**:
  - Resolves local Test262 suite path (env override + repo-root discovery).
  - Configures `Test262StreamLoader` for generated fixture initialization.
- **`TestHarness.InitializeCustomState`**:
  - Caches harness include sources (`assert.js`, `sta.js`, and optional includes) for deterministic execution.
- **`Test262Test` partial**:
  - **`BuildTestExecutor`**: creates isolated FenRuntime realms and wires host-defined globals.
  - **`ExecuteTest`**: runs script/module tests and normalizes runtime throw/error completion into NUnit failures.
  - **`ShouldThrow`**: maps Test262 negative metadata to generated fixture throw expectations.

#### `FenBrowser.Test262/generate_test262.ps1`

Deterministic generation script for Test262 NUnit fixtures.

- Restores local tool (`test262`).
- Normalizes Windows path to Test262Harness-compatible `/mnt/<drive>/...` format.
- Regenerates `FenBrowser.Test262/Generated` using `Test262Harness.settings.json`.

#### `WPTTestRunner.cs`

The Web Platform Tests (WPT) runner for DOM/CSS compliance.

- Automates the execution of `.html` tests and compares rendered output or computed styles against reference expectations.

#### `AcidTestRunner.cs`

Specialized harness for the Acid2/Acid3 verification suites.

### 4.3 Contributor Cookbook: Adding a WebDriver Command

To implement a new command (e.g., `GET /session/{id}/print`):

1.  **Define the Command Logic**:
    - Create a class implementing `ICommand` in `FenBrowser.WebDriver.Commands`.
    - Implement `ExecuteAsync(Session session, Dictionary<string, object> parameters)`.

2.  **Register the Route**:
    - In `CommandRouter.cs`, add the route mapping:
      ```csharp
      _routes.Add(("GET", "/session/{sessionId}/print"), new PrintPageCommand());
      ```

3.  **Implement the Bridge**:
    - If the command requires Engine interaction, add a method to `IBrowser` interface.
    - Implement it in `BrowserApi.cs` (Engine side) and `BrowserHost` (Host side).

### 4.4 Phase-0 Security Hardening (2026-02-18)

- `FenBrowser.WebDriver/WebDriverServer.cs`
  - Replaced wildcard CORS behavior with validated-origin echo behavior.
  - Added strict request validation using `OriginValidator` for:
    - remote endpoint loopback validation
    - `Origin` header validation for browser-driven requests
  - Preflight handling now returns `204` only after validation.

- `FenBrowser.WebDriver/Security/OriginValidator.cs`
  - Strengthened `Origin` parsing:
    - only `http`/`https` schemes accepted
    - localhost/loopback-only enforcement when `allowLocalhostOnly` is enabled
    - explicit loopback IP handling

- `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  - Added per-session security context bootstrap for all session-scoped commands.
  - Wired `CapabilityGuard` and `SandboxEnforcer` lifecycle:
    - create on session creation/use
    - destroy on session deletion

- `FenBrowser.WebDriver/Commands/NavigationCommands.cs`
  - Added URL policy enforcement via `CommandHandler.IsNavigationAllowed(...)` before browser navigation.

- `FenBrowser.WebDriver/Commands/ScriptCommands.cs`
  - Added script policy enforcement via `CommandHandler.IsScriptAllowed(...)` before sync/async execution.

- `FenBrowser.Host/ChromeManager.cs`
  - WebDriver server startup is now disabled by default in normal host startup.
  - Enable explicitly via environment variables:
    - `FEN_WEBDRIVER=1`
    - `FEN_WEBDRIVER_PORT` (optional, default `4444`)
  - Replaced reflection-based driver injection with direct `WebDriverServer.SetDriver(...)`.

### 4.5 Phase-3 Verification Truthfulness (2026-02-18)

- `FenBrowser.FenEngine/Testing/WPTTestRunner.cs`
  - Runner no longer treats zero-assertion runs as implicit success.
  - Test result success now requires both:
    - at least one assertion reported
    - harness completion signal (notifyDone / parsed harness status / settled results).
  - Timeout waiting for async completion is now surfaced as explicit test failure.

- `FenBrowser.Tests/*`
  - Removed `Assert.True(true)` placeholders and replaced them with observable behavior assertions.

- `.github/workflows/build-fenbrowser-exe.yml`
  - Added CI verification guard step.

- `scripts/ci/verify-verification-guards.ps1`
  - Fails CI on placeholder assertions.
  - Fails CI on stale `WptRunner.cs` doc references.
  - Fails CI when `docs/VERIFICATION_BASELINES.md` drifts from `test262_results.md` metrics.

### 4.6 Phase-5 WebDriver Coverage Guard (2026-02-18)

- `FenBrowser.WebDriver/CommandRouter.cs`
  - Added route/command introspection for registered commands and route counts.

- `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  - Added explicit manifest of currently implemented commands for parity checks.

- `FenBrowser.WebDriver/WebDriverServer.cs`
  - Added startup coverage diagnostics:
    - total routes
    - unique routed commands
    - implemented command count
    - missing and extra command lists
  - Added strict startup gate for parity enforcement:
    - set `FEN_WEBDRIVER_STRICT_COMMAND_COVERAGE=1` to fail startup when coverage is incomplete.

### 4.7 Phase-5 WebDriver Command Completion (2026-02-18)

- `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  - Added concrete handling for the previously missing cookie/action/alert/print/window-context/element-state commands.
  - Implemented-command manifest now reaches full route parity.

- `FenBrowser.WebDriver/Commands/ElementCommands.cs`
  - Added:
    - `FindElementFromElement`, `FindElementsFromElement`
    - active-element and element-state/property/css/tag/rect/enabled/role/label routes
    - element clear + element screenshot routes.

- `FenBrowser.WebDriver/Commands/WindowCommands.cs`
  - Added:
    - switch/new window routes
    - switch frame/parent frame routes
    - maximize/minimize/fullscreen routes.

- `FenBrowser.Host/WebDriver/FenBrowserDriver.cs`
- `FenBrowser.Host/WebDriver/HostBrowserDriver.cs`
  - Expanded driver adapters to implement the complete Phase-5 WebDriver command surface.

- Coverage snapshot after completion:
  - `RouteCommands=58`
  - `ImplementedCommands=58`
  - `MissingCount=0`

### 4.8 Remaining Findings Tranche - CSP Origin Tests (2026-02-19)

- Added:
  - `FenBrowser.Tests/Core/Network/CspPolicyTests.cs`
- Coverage in new tests:
  - `'self'` allow when explicit same-origin context is provided.
  - `'self'` deny when origin context is missing.
  - wildcard subdomain allow behavior.
  - explicit port mismatch deny behavior.

### 4.9 Phase-Completion Tranche - Structured WPT + Module Loader Tests (2026-02-19)

- Added:
  - `FenBrowser.Tests/WebAPIs/TestHarnessApiTests.cs`
  - `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
- Coverage in new tests:
  - structured test harness snapshot tracks completion + result events.
  - module loader resolves exact import-map entries.
  - module loader resolves prefix import-map entries.
  - extensionless relative HTTP module specifiers normalize to `.js`.

### 4.10 Phase-6 Hardening Regression Additions (2026-02-19)

- Updated:
  - `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs`
    - reflects live dispatch path semantics (dispatch present; no-`respondWith` cases still fall back to network).

- Added:
  - `FenBrowser.Tests/Workers/WorkerTests.cs`
    - verifies worker constructor rejects `file://` script URLs.
  - `FenBrowser.Tests/Storage/StorageBackendTests.cs`
    - verifies traversal-like IndexedDB names are sanitized into safe in-root file paths.

_End of Volume VI_
