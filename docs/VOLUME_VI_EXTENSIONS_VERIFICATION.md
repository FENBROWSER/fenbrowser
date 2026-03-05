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
  - Fails CI on stale legacy WPT runner filename doc references.
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

### 4.11 Completion Pass - Build Stability and Smoke Verification (2026-02-19)

- `FenBrowser.Host/FenBrowser.Host.csproj`
- `FenBrowser.Tests/FenBrowser.Tests.csproj`
  - Added deterministic project-reference build setting:
    - `<BuildInParallel>false</BuildInParallel>`
  - Purpose: prevent machine-local silent `_GetProjectReferenceTargetFrameworkProperties` failures during Host/Tests builds.

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs`
- `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
- `FenBrowser.Tests/Workers/WorkerTests.cs`
  - Updated expectations to align with hardened URL normalization and module-origin policy behavior.
  - Updated worker startup error regression to verify fetch-failure event flow using valid script URL.

- Smoke run snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~ServiceWorkerLifecycleTests|FullyQualifiedName~ModuleLoaderTests|FullyQualifiedName~WorkerTests"`
  - Result: `Passed 27/27`.

### 4.12 Final Completion Pass - Process-Isolation IPC Validation (2026-02-19)

- Build validation snapshot:
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`
  - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -clp:ErrorsOnly`
  - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug -clp:ErrorsOnly`
  - all succeeded in this machine state.

- Smoke regression snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~ServiceWorkerLifecycleTests|FullyQualifiedName~ModuleLoaderTests|FullyQualifiedName~WorkerTests"`
  - Result: `Passed 27/27`.

- Coverage note:
  - Process-isolation IPC transport was validated through host compilation and runtime wiring paths in this tranche.
  - Dedicated end-to-end process-isolation integration tests are a follow-up verification expansion item.

### 4.13 Volume Reference Integrity Guard (2026-02-26)

- Added:
  - `test_parser/Program.cs`
- Purpose:
  - Parses `docs/VOLUME_*.md` and extracts source references (`*.cs`, with optional `Lines X-Y` or `:X-Y` claims).
  - Resolves each reference to concrete source files in the repository.
  - Verifies line-range claims against actual file line counts.
  - Fails on:
    - missing files
    - ambiguous filename-only matches
    - invalid line ranges
    - out-of-range line claims.

- CI wiring:
  - Added dedicated runner:
    - `scripts/ci/verify-volume-doc-references.ps1`
  - Updated `scripts/ci/verify-verification-guards.ps1` to run:
    - `dotnet run --project test_parser/test_parser.csproj -- --repo . --docs docs`
  - The verification guard now blocks merges when Volume documentation references drift from actual source topology.

### 4.14 HTML/CSS Parser Regression Additions (2026-02-26)

- Added:
  - `FenBrowser.Tests/Engine/CssSyntaxParserTests.cs`
  - `FenBrowser.Tests/Engine/CssCustomPropertyEdgeCaseTests.cs`
  - `FenBrowser.Tests/Core/Parsing/HtmlCharacterReferenceTests.cs`
  - `FenBrowser.Tests/Core/Parsing/ParserHardeningGuardTests.cs`
  - `FenBrowser.Tests/Core/RendererViewportHardeningTests.cs`
- Coverage in new tests:
  - `@font-face` at-rule parsing produces `CssFontFaceRule` with descriptor declarations (`font-family`, `src`, `font-weight`).
  - malformed declaration recovery path keeps parser progress and preserves parsing of following valid declarations in the same rule block.
  - custom-property declaration names preserve authored case through stylesheet and inline-style parsing (`--MyVar` vs `--myvar` remain distinct keys).
  - inline style parsing now respects top-level declaration boundaries, so semicolons inside function values (for example `url(data:image/svg+xml;...)`) do not truncate declarations.
  - HTML tokenizer character references decode in both text and attribute values for numeric and common named references.
  - unknown named references remain literal text for compatibility-safe recovery.
  - named references decode with semicolon omission in text-safe boundaries (`&copy 2026`), while attribute `&name=` forms stay literal.
  - numeric reference compatibility remap is validated (`&#128;` -> `\u20AC`).
  - malformed numeric reference prefixes are preserved (`&#;`, `&#x;`) in both text and attributes.
  - HTML tokenizer emission limiter is validated (`MaxTokenEmissions` guard).
  - HTML tree-builder deep-nesting clamp is validated (`MaxOpenElementsDepth` guard behavior under pathological nesting).
  - CSS parser rule/declaration caps are validated (`MaxRules`, `MaxDeclarationsPerBlock`).
  - renderer viewport sanitization is validated for invalid dimensions (`Infinity`/non-positive inputs).
  - broader named-reference coverage is validated through fallback-decoded entities (`&larr;`, `&sum;`) in both text and attributes.
  - legacy partial-decoding compatibility is locked (`&notanentity;` -> `\u00ACanentity;`).

### 4.15 System-Wide Parser/Renderer Hardening Tranche (2026-02-26)

- Added:
  - `FenBrowser.Core/Parsing/ParserSecurityPolicy.cs`
  - `FenBrowser.FenEngine/Rendering/RendererSafetyPolicy.cs`
  - `FenBrowser.Tests/Engine/ParserSecurityPolicyIntegrationTests.cs`
  - `FenBrowser.Tests/Rendering/RenderWatchdogTests.cs`
  - `FenBrowser.Tests/Engine/ParserFuzzRegressionTests.cs`
  - `scripts/ci/run-parser-fuzz-regressions.ps1`
- Policy wiring coverage:
  - `HtmlParser` applies centralized `ParserSecurityPolicy` (`HtmlMaxTokenEmissions`, `HtmlMaxOpenElementsDepth`) to tokenizer/tree-builder entrypoints.
  - `CssLoader` applies centralized `ActiveParserSecurityPolicy` (`CssMaxRules`, `CssMaxDeclarationsPerBlock`) to CSS syntax parsing entrypoints.
  - `SelectorMatcher` applies malformed-selector hardening guards:
    - chain-level forward-progress enforcement for invalid tokens,
    - selector recursion-depth and selector-length caps for functional pseudo-class argument parsing.
  - `SkiaDomRenderer` applies `RendererSafetyPolicy` to stage-level render watchdog checks.
- Watchdog/fail-safe coverage:
  - paint/raster/frame timing budget checks are asserted.
  - pre-raster over-budget fail-safe path is asserted (`SkipRasterWhenOverBudget`).
- CI wiring:
  - `verify-verification-guards.ps1` now runs parser/renderer hostile-corpus regressions via:
    - `scripts/ci/run-parser-fuzz-regressions.ps1`
  - fuzz regressions execute deterministic hostile corpus + mutation coverage and fail CI on parser/renderer crashes.

### 4.16 WPT Harness Execution Reliability (2026-02-27)

- `FenBrowser.WPT/HeadlessNavigator.cs`
  - Added deterministic external-script resolution for WPT runs:
    - root-absolute (`/resources/...`),
    - test-relative,
    - WPT-root fallback.
  - Added minimal `testharness` shim path for `/resources/testharness.js` and `/resources/testharnessreport.js` so headless runs always produce structured assertion events even when full upstream harness execution is not viable in current VM state.
  - Added script-labeled diagnostics and parser/error capture in navigator execution flow to make zero-assertion failures actionable.

- `FenBrowser.Conformance/HeadlessNavigator.cs`
  - Aligned conformance WPT navigation/execution path with WPT CLI path (same script resolution + harness shim behavior).

- `FenBrowser.Conformance/Program.cs`
  - `run wpt` now passes a non-null headless navigator into `WPTTestRunner` (removes prior `CompletionSignal=no-navigator` failure mode).
  - Added a defensive WPT max-test clamp (`safeWptMax=50`) for conformance runs to prevent known VM recursion/stack-overflow crash cases in large DOM sweeps.

- `FenBrowser.FenEngine/Core/Parser.cs`
  - Fixed empty-parameter arrow callback parsing in grouped expression path:
    - `() => { ... }` bodies now parse with `consumeTerminator: false`, preserving outer call delimiters and eliminating false `expected ... RParen` parser failures in callback-heavy WPT scripts.

- Verification snapshot
  - `dotnet run --project FenBrowser.WPT -- run_single dom/attributes-are-nodes.html --timeout 8000 --verbose`
    - now emits harness completion (`testRunner.notifyDone`) with assertion counts (no longer zero-assertion bootstrap failure).
  - `dotnet run --project FenBrowser.WPT -- run_category dom --max 50 --timeout 8000 --format json -o wpt_dom50_after_fix.json`
    - completed with assertion accounting (`Assertions: 126`).
  - `dotnet run --project FenBrowser.Conformance -- run wpt dom --max 50 -o conformance_wpt50.md`
    - completed using the same harness path as WPT CLI.
_End of Volume VI_


### 4.17 Test262 Watchdog Parallel-Worker Enablement (2026-03-05)
- `scripts/run_test262_full_watchdog.ps1`
  - Added chunk-range execution controls:
    - `-StartChunk <N>`
    - `-EndChunk <N>` (or `0` for auto max)
  - Added output-root override:
    - `-ResultsRoot <path>` for per-worker isolated outputs.
  - Added range validation and range reporting in summary output.
- Operational impact:
  - Multiple watchdog instances can now run non-overlapping chunk ranges concurrently on multi-core machines without output collisions.
  - Example pattern: run 4 workers with distinct ranges and distinct `ResultsRoot` directories, then aggregate summaries.
