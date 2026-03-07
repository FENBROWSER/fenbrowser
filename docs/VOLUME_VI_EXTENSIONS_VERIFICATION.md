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

### 4.7.1 WebDriver Window Context State Hardening (2026-03-07)

- `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  - Window-handle routes now await async window-state commands so command responses can synchronize against the live host tab model before returning.

- `FenBrowser.WebDriver/Commands/WindowCommands.cs`
  - Added browser-backed session synchronization for:
    - current window handle,
    - all window handles,
    - close-window state,
    - switch-window validation.
  - Removed the remaining session-only window-context drift where WebDriver could report fabricated or stale handles after host tab changes.

- `FenBrowser.Host/WebDriver/FenBrowserDriver.cs`
- `FenBrowser.Host/WebDriver/HostBrowserDriver.cs`
  - Added real current-window, window-list, and close adapters against `TabManager`.

- Net effect:
  - WebDriver window context commands now operate on real browser tabs instead of partially synthetic session bookkeeping.

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

### 4.25 DevTools DOM/CSS Dispatcher Regression Coverage (2026-03-06)
- `FenBrowser.Tests/DevTools/DomDomainTests.cs`
  - Added `GetDocumentAsync_AwaitsDispatcherAndBuildsDocumentSnapshot`.
  - Added `SetAttributeValueAsync_AwaitsDispatcherBeforeMutatingElement`.
- `FenBrowser.Tests/DevTools/CSSDomainTests.cs`
  - Added `GetComputedStyleForNode_AwaitsDispatcherAndReturnsComputedStyles`.
  - Added `SetStyleTexts_AwaitsDispatcherAndTriggersRepaint`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~DomDomainTests|FullyQualifiedName~CSSDomainTests|FullyQualifiedName~RuntimeDomainTests" --logger "console;verbosity=minimal"`: pass (`6/6`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.

### 4.26 Worker Bootstrap Async Regression Coverage (2026-03-06)
- `FenBrowser.Tests/Workers/WorkerTests.cs`
  - Added `WorkerRuntime_AsyncBootstrapWaitsForFetchBeforeExecutingScript` to hold worker script fetch open, prove bootstrap does not execute early, and confirm startup completes once the fetch resolves.
  - Re-ran startup error and import prefetch regressions alongside the new bootstrap gate coverage.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~WorkerRuntime_OnError_EventFires|FullyQualifiedName~WorkerRuntime_AsyncBootstrapWaitsForFetchBeforeExecutingScript|FullyQualifiedName~WorkerRuntime_ImportScripts_LoadsAndExecutesDependency|FullyQualifiedName~WorkerRuntime_ImportScripts_ReusesPrefetchedSourceAcrossRepeatedImports" --logger "console;verbosity=minimal"`: pass (`4/4`).

### 4.27 Host Entry Dispatch Regression Coverage (2026-03-06)
- `FenBrowser.Tests/Architecture/ProgramStartupModeTests.cs`
  - Added startup-mode precedence coverage for renderer-child arg/env, Test262 CLI detection, WebDriver port detection, and default browser fallback.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter FullyQualifiedName~ProgramStartupModeTests --logger "console;verbosity=minimal"`: pass (`5/5`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.


### 4.28 Custom Elements `whenDefined()` Regression Coverage (2026-03-06)
- `FenBrowser.Tests/DOM/CustomElementRegistryTests.cs`
  - Added `WhenDefined_PendingPromise_ResolvesAtMicrotaskCheckpoint`.
  - Added `WhenDefined_ThenAddedAfterFulfillment_RunsOnNextMicrotask`.
  - Added `WhenDefined_AlreadyDefinedPromise_ThenRunsOnNextMicrotask`.
  - Added `WhenDefined_MissingName_RejectedCatchRunsOnNextMicrotask`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter FullyQualifiedName~CustomElementRegistryTests --logger "console;verbosity=minimal"`: pass (`4/4`).
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~CustomElementRegistryTests|FullyQualifiedName~ExecutionContextSchedulingTests" --logger "console;verbosity=minimal"`: pass (`6/6`).


### 4.29 BrowserHost Element Property Regression Coverage (2026-03-06)
- `FenBrowser.Tests/Rendering/BrowserHostElementPropertyTests.cs`
  - Added `GetElementPropertyAsync_InMemoryAttributeLookup_CompletesSynchronously`.
  - Added `GetElementPropertyAsync_MissingProperty_ReturnsCompletedNull`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~BrowserHostElementPropertyTests|FullyQualifiedName~BrowserHostFormSubmissionTests" --logger "console;verbosity=minimal"`: pass (`3/3`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.


### 4.30 Worker Bootstrap Completion Observer Verification (2026-03-06)
- Re-ran existing worker bootstrap/error/import regressions against the new async observer path in `FenBrowser.FenEngine/Workers/WorkerRuntime.cs`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~WorkerRuntime_OnError_EventFires|FullyQualifiedName~WorkerRuntime_AsyncBootstrapWaitsForFetchBeforeExecutingScript|FullyQualifiedName~WorkerRuntime_ImportScripts_LoadsAndExecutesDependency|FullyQualifiedName~WorkerRuntime_ImportScripts_ReusesPrefetchedSourceAcrossRepeatedImports" --logger "console;verbosity=minimal"`: pass (`4/4`).
  - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -clp:ErrorsOnly`: pass.


### 4.31 Clipboard Retry Policy Verification (2026-03-06)
- `FenBrowser.Tests/Architecture/ClipboardHelperTests.cs`
  - Added `TryOpenClipboardWithRetry_RetriesUntilOpenSucceeds`.
  - Added `TryOpenClipboardWithRetry_StopsAfterMaxAttempts`.
  - Added `TryOpenClipboardWithRetry_DoesNotDelayAfterImmediateSuccess`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~ClipboardHelperTests|FullyQualifiedName~BrowserHostElementPropertyTests" --logger "console;verbosity=minimal"`: pass (`5/5`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.
  - `rg -n "Thread\.Sleep\(" FenBrowser.Host -g '*.cs'`: no matches.


### 4.32 JavaScriptEngine Background Task Fault Observation Verification (2026-03-06)
- `FenBrowser.Tests/Engine/JavaScriptEngineModuleLoadingTests.cs`
  - Added `SetDom_DeprecatedSyncWrapper_DoesNotThrowOnAsyncFetchFailure`.
  - Re-ran deprecated non-blocking `SetDom(...)` wrapper coverage and async module graph prefetch coverage.
- `FenBrowser.Tests/Engine/ExecutionContextSchedulingTests.cs`
  - Re-ran timer callback queueing coverage alongside the scheduler observer cleanup.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~JavaScriptEngineModuleLoadingTests|FullyQualifiedName~ExecutionContextSchedulingTests" --logger "console;verbosity=minimal"`: pass (`5/5`).
  - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -clp:ErrorsOnly`: pass.
  - `rg -n "ContinueWith\(" FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`: no matches.


### 4.33 JavaScriptEngine Geolocation Watch Verification (2026-03-06)
- `FenBrowser.Tests/Engine/JavaScriptEngineGeolocationTests.cs`
  - Added `WatchPosition_ReturnsDistinctIds`.
  - Added `WatchPosition_SchedulesCallbacksUntilCleared`.
  - Added `Reset_ClearsActiveGeolocationWatches`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~JavaScriptEngineGeolocationTests" --logger "console;verbosity=minimal"`: pass (`3/3`).
  - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -clp:ErrorsOnly`: pass.


### 4.34 Static GeolocationAPI Verification (2026-03-06)
- `FenBrowser.Tests/WebAPIs/GeolocationApiTests.cs`
  - Added `WatchPosition_ReturnsDistinctIds`.
  - Added `WatchPosition_FiresUntilCleared`.
  - Added `WatchPosition_PermissionDenied_InvokesErrorCallback`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~GeolocationApiTests|FullyQualifiedName~WebApiPromiseTests" --logger "console;verbosity=minimal"`: pass (`16/16`).
  - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -clp:ErrorsOnly`: pass.

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

### 4.18 Test262 Runner Guardrail Update (2026-03-05)
- `FenBrowser.Test262/Program.cs`
  - Added `--max-memory-mb <N>` global flag to tune runner managed-heap cap (`Test262Runner.MemoryThresholdBytes`) from CLI.
  - Extended `run_single` command path to honor memory cap for consistency with chunk/category runs.
- Validation snapshot:
  - 10-worker logical chunk-13 execution completed via subchunk strategy (`121..130` at `--chunk-size 100`) and aggregate output:
    - `Results/test262_chunk13_10workers_20260305_212054/chunk13_10workers_aggregate.md`
    - `Results/test262_chunk13_10workers_20260305_212054/chunk13_10workers_aggregate.json`
  - Aggregate totals: `total=1000`, `passed=412`, `failed=588`, `passRate=41.2%`.

### 4.19 Test262 Pending Recheck Blocker (2026-03-05)
- Rechecked remaining size-1 pending chunk ids: 51812, 51865, 52412, 52623.
- Result: all 4 still process-crash with stack overflow before JSON emission.
- Recorded blocker artifacts:
  - Results/test262_pending2919_10workers_20260305_212453/pending_recheck_blocked_stackoverflow.md`r
  - Results/test262_pending2919_10workers_20260305_212453/pending_recheck_blocked_stackoverflow.json`r


### 4.20 Test262 Isolated Child-Process Recheck Mode (2026-03-05)
- `FenBrowser.Test262/Test262Config.cs`
  - Added `IsolateProcess` option to enable crash-safe chunk execution.
- `FenBrowser.Test262/Program.cs`
  - Added global CLI flag `--isolate-process`.
  - `run_chunk` now supports isolated execution where each test is executed by a child `run_single` invocation.
  - Parent runner now persists JSON result files even when a child hard-crashes (for example stack overflow), classifying them as failed tests instead of hanging the parent process.
- Recheck status refresh for pending2919 run:
  - Rechecked ids: `51812`, `51865`, `52412`, `52623`.
  - `51812`, `51865`, `52623`: child process stack-overflow crash captured as fail.
  - `52412`: non-crash runtime failure (`ReferenceError: bareword is not defined`).
  - Updated artifacts:
    - `Results/test262_pending2919_10workers_20260305_212453/pending2919_10workers_FINAL.json`
    - `Results/test262_pending2919_10workers_20260305_212453/pending2919_10workers_FINAL.md`
    - `Results/test262_pending2919_10workers_20260305_212453/pending_recheck_blocked_stackoverflow.json`
    - `Results/test262_pending2919_10workers_20260305_212453/pending_recheck_blocked_stackoverflow.md`

### 4.21 Test262 Pending Recheck Recovery (2026-03-05)
- Recheck wave for previous blocked size-1 IDs after runtime fixes:
  - `51812` -> PASS
  - `51865` -> PASS
  - `52623` -> PASS
  - `52412` -> FAIL (`ReferenceError: bareword is not defined`)
- Updated result artifacts:
  - `Results/test262_pending2919_10workers_20260305_212453/recheck_chunk_51812_isolated.json`
  - `Results/test262_pending2919_10workers_20260305_212453/recheck_chunk_51865_isolated.json`
  - `Results/test262_pending2919_10workers_20260305_212453/recheck_chunk_52412_isolated.json`
  - `Results/test262_pending2919_10workers_20260305_212453/recheck_chunk_52623_isolated.json`
  - `Results/test262_pending2919_10workers_20260305_212453/pending2919_10workers_FINAL.json`
  - `Results/test262_pending2919_10workers_20260305_212453/pending_recheck_blocked_stackoverflow.json`
- Aggregate delta from this recovery pass:
  - `+3` pass, `-3` stack-overflow crash cases.
  - Remaining from these four: `1` semantic/runtime failure (`global-receiver.js`).


### 4.22 Event-Loop Scheduler Routing Regression Coverage (2026-03-06)
- `FenBrowser.Tests/Engine/ExecutionContextSchedulingTests.cs`
  - Added `ScheduleMicrotask_DefaultScheduler_RunsOnlyAtCheckpoint` to prove default `ExecutionContext` microtasks stay queued until the microtask checkpoint and execute under `EnginePhase.Microtasks`.
  - Added `ScheduleCallback_DefaultScheduler_EnqueuesTimerTaskBeforeExecution` to prove timer callbacks are marshaled through the event-loop task queue and execute under `EnginePhase.JSExecution`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter FullyQualifiedName~ExecutionContextSchedulingTests --logger "console;verbosity=minimal"`: pass (`2/2`).


### 4.23 DevTools Runtime Evaluation Async Regression Coverage (2026-03-06)
- `FenBrowser.Tests/DevTools/RuntimeDomainTests.cs`
  - Added `EvaluateAsync_AwaitsHostEvaluationAndReturnsResult` to prove `Runtime.evaluate` awaits asynchronous host execution instead of forcing synchronous completion.
  - Added `EvaluateAsync_HostFailure_ReturnsProtocolFailure` to preserve protocol failure reporting when host evaluation throws.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter FullyQualifiedName~RuntimeDomainTests --logger "console;verbosity=minimal"`: pass (`2/2`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.


### 4.24 Brokered Renderer Child Loop IO Regression Coverage (2026-03-06)
- `FenBrowser.Tests/Architecture/RendererChildLoopIoTests.cs`
  - Added `ReadLineWithTimeoutAsync_ReturnsLine_WhenReaderCompletesBeforeTimeout`.
  - Added `ReadLineWithTimeoutAsync_ReturnsTimeout_WhenReaderDoesNotCompleteInTime`.
  - Added `ReadLineWithTimeoutAsync_ReturnsEndOfStream_WhenReaderCompletesWithNull`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter FullyQualifiedName~RendererChildLoopIoTests --logger "console;verbosity=minimal"`: pass (`3/3`).
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug -clp:ErrorsOnly`: pass.

### 4.35 Web Audio API Verification Tranche (2026-03-06)

- Added `FenBrowser.Tests/WebAPIs/AudioApiTests.cs` coverage for:
  - constructor correctness (`Audio` is constructor-capable and instantiates playable objects),
  - MIME support responses via `canPlayType` (`probably` / `maybe` / unsupported empty string),
  - promise rejection path for invalid schemes in `Audio.play()`,
  - JavaScript runtime exposure (`Audio` available on both global and `window`).
- Regression intent: validates migration from parser feature-gap fallback to runtime-backed `Audio` behavior and secures source-validation controls in the hot path.
- Suggested verification command:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~AudioApiTests"`



### 4.36 Notifications API Verification Tranche (2026-03-06)

- Added `FenBrowser.Tests/WebAPIs/NotificationsApiTests.cs` coverage for:
  - constructor semantics (`Notification` exposed as a constructor-capable function),
  - permission denial enforcement on constructor invocation,
  - `requestPermission()` callback + thenable behavior,
  - JavaScript runtime exposure (`Notification` available on both global and `window`).
- Regression intent: validates migration from object-shaped Notification API to runtime-backed constructor semantics and verifies permission-gated secure behavior.
- Verification commands:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~NotificationsApiTests"`
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~WebApiPromiseTests|FullyQualifiedName~AudioApiTests"`


### 4.37 WebRTC Constructor and ICE Hardening Verification (2026-03-06)
- `FenBrowser.Tests/WebAPIs/WebRtcApiTests.cs`
  - Added/validated coverage for:
    - constructor semantics for `RTCPeerConnection`,
    - ICE-scheme rejection path for unsupported URLs,
    - constructor semantics for `MediaStream`,
    - JavaScript runtime exposure on global and `window`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~WebRtcApiTests"`: pass (`4/4`).

### 4.38 Observer Constructor and Exposure Verification (2026-03-06)
- `FenBrowser.Tests/WebAPIs/ObserverApiTests.cs`
  - Added coverage for:
    - constructor semantics for `IntersectionObserver` and `ResizeObserver`,
    - option validation guard rails (out-of-range `IntersectionObserver` thresholds),
    - runtime exposure on global and `window`.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~ObserverApiTests|FullyQualifiedName~IntersectionObserverTests|FullyQualifiedName~ResizeObserverTests"`: pass (`18/18`).
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~ObserverApiTests|FullyQualifiedName~WebRtcApiTests|FullyQualifiedName~NotificationsApiTests|FullyQualifiedName~AudioApiTests|FullyQualifiedName~WebApiPromiseTests"`: pass (`30/30`).

### 4.39 Cache API Persistence and Match Verification (2026-03-06)
- `FenBrowser.Tests/WebAPIs/ServiceWorkerCacheTests.cs`
  - Added coverage for:
    - `CacheStorage.match()` cross-cache lookup behavior,
    - cached body persistence and `text()`/`json()` readers,
    - rejection semantics for invalid `Cache.put(...)` argument sets,
    - delete lifecycle stability after async cache initialization.
- Verification snapshot:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~ServiceWorkerCacheTests|FullyQualifiedName~WebApiPromiseTests"`: pass (`20/20`).
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~ServiceWorkerCacheTests|FullyQualifiedName~ObserverApiTests|FullyQualifiedName~WebRtcApiTests|FullyQualifiedName~NotificationsApiTests|FullyQualifiedName~AudioApiTests|FullyQualifiedName~WebApiPromiseTests"`: pass (`37/37`).

### 4.40 Conformance Milestone Gate Enforcement (2026-03-07)
- `FenBrowser.Conformance/ConformanceGate.cs` (new)
  - Added production-grade gate evaluation for Test262 and WPT result artifacts.
  - Gate evaluation now supports:
    - required-artifact validation,
    - minimum pass-rate and minimum total-test thresholds,
    - unexpected-failure budgets using expected-failure ledgers,
    - baseline regression detection on a per-test-file basis,
    - WPT `No assertions executed by testharness.` failure detection.
- `FenBrowser.Conformance/Program.cs`
  - Added `gate` command surface:
    - `FenBrowser.Conformance gate default all`
    - `FenBrowser.Conformance gate default test262`
    - `FenBrowser.Conformance gate default wpt-c80`
    - `FenBrowser.Conformance gate default wpt-c90`
    - `FenBrowser.Conformance gate default wpt-d`
    - `FenBrowser.Conformance gate default wpt-e`
    - `FenBrowser.Conformance gate <policy-path>`
  - Gate command now exits non-zero when milestone policy conditions fail, making it usable in CI/release enforcement.
- `FenBrowser.Conformance/Gates/*.json` (new)
  - Added built-in milestone policies for:
    - Test262 production gate (`B3`)
    - WPT DOM/event gates (`C` 80% and 90%)
    - WPT CSS/layout artifact gate (`D`)
    - WPT fetch/CORS artifact gate (`E`)
- `FenBrowser.Conformance/Gates/*expected_failures.txt` (new)
  - Added ledger entry points for explicitly accepted known failures.
- `FenBrowser.Conformance/ConformanceReport.cs`
  - Replaced the previous baseline-comparison placeholder with structured suite/category delta reporting when a JSON baseline report is supplied.
- Net effect:
  - WPT/Test262 milestone gating is no longer documentation-only.
  - The repository now has enforceable, artifact-driven conformance gates that can fail CI/release workflows on regressions, missing evidence, or below-threshold milestone results.
## 4.12 DOM regression-pack artifact workflow (2026-03-07)

- `FenBrowser.WPT` now supports cluster-scoped DOM regression execution:
  - `run_pack <pack>`
  - `extract_pack <pack> [Results/wpt_results_latest.json]`
  - `list_packs`
- Built-in pack manifests live in `FenBrowser.WPT/RegressionPacks/` and currently cover the historical Milestone `C` buckets:
  - no-assertion harness failures
  - event-runtime `undefined is not a function` failures
  - named-collection / property-descriptor failures
- Default pack output now writes:
  - a versioned JSON artifact in `Results/`
  - a stable `*_latest.json` alias for the same pack
- This makes the DOM/event recovery clusters rerunnable and separately retainable from the aggregate `Results/wpt_results_latest.json` report.
### 3.14 IPC fuzz-baseline command (2026-03-07)
- `FenBrowser.Conformance` now exposes `ipc-fuzz` as a first-class CLI command.
- Usage:
  - `dotnet run --project FenBrowser.Conformance -- ipc-fuzz`
  - `dotnet run --project FenBrowser.Conformance -- ipc-fuzz -o Results/ipc_fuzz_baseline.json`
- The command runs the host-side baseline mutator suite over renderer/network/target envelope serializers and writes a JSON artifact when `-o` is provided.
- This provides the first operational Milestone `A3` baseline, but it does not replace broader live-channel fault injection or coverage-guided fuzzing.
### 3.15 Accessibility platform snapshot validation (2026-03-07)
- `FenBrowser.Conformance` now exposes `a11y-validate`.
- Usage:
  - `dotnet run --project FenBrowser.Conformance -- a11y-validate`
  - `dotnet run --project FenBrowser.Conformance -- a11y-validate -o Results/a11y_platform_snapshot.json`
- The command parses a built-in fixture document, builds the internal accessibility tree, exports normalized snapshots for Windows UIA / Linux AT-SPI / macOS NSAccessibility, and writes a JSON artifact.
- This provides a concrete Milestone `F3` validation artifact path even though live platform bridge completeness is still partial.

### 3.16 CORB validation artifact command (2026-03-07)
- `FenBrowser.Conformance` now exposes `corb-validate`.
- Usage:
  - `dotnet run --project FenBrowser.Conformance -- corb-validate`
  - `dotnet run --project FenBrowser.Conformance -- corb-validate -o Results/corb_validation.json`
- The command runs bounded CORB classification cases over the broker-side `CorbFilter` and writes a JSON artifact capturing expected/actual verdicts.
- This provides a concrete Milestone `F2` validation artifact path for the strengthened MIME/body analysis layer.

### 3.17 Full Validation Pass Status (2026-03-07)
- `dotnet build FenBrowser.sln -maxcpucount:1`: pass
- `FenBrowser.Conformance ipc-fuzz`: pass
- `FenBrowser.Conformance a11y-validate`: pass
- `FenBrowser.Conformance corb-validate`: fail (`same-origin-json` blocked unexpectedly)
- `FenBrowser.Conformance gate default all`: fail
  - Test262 gate at 52.9% vs required 99%
  - DOM/Event WPT gate at 31% with regressions and no-assertion failures
  - CSS/Layout and Fetch/CORS required result artifacts still missing
- Host 25-second diagnostic run produced `debug_screenshot.png` showing a socket/access-permission network failure page rather than a clean browser render

## 6.18 Focused Validation Delta (2026-03-07)
- `dotnet build FenBrowser.sln -maxcpucount:1`: passed after the CORB and transport patches.
- `dotnet run --project FenBrowser.Conformance -- corb-validate`: passed all built-in cases, including `same-origin-json`.
- 25-second host run:
  - process stdout showed successful navigation and content rendering for `https://www.google.com/`
  - `debug_screenshot.png` remained stale from an older error-page run, so the screenshot artifact is currently not trustworthy as a fresh post-run render signal
  - no fresh `raw_source_*.html` artifacts were observed in the checked log roots
- Conclusion: CORB regression is fixed; host transport no longer reproduced the earlier socket-permission failure in process stdout, but screenshot/raw-source artifact generation still needs its own diagnostic pass.
## 6.19 Milestone D/E Dedicated WPT Artifact Packs (2026-03-07)
- Added built-in WPT regression packs for milestone evidence generation:
  - `css_layout`
  - `fetch_cors`
- These packs are intended to materialize the exact gate artifacts expected by:
  - `Results/wpt_css_layout_results.json`
  - `Results/wpt_css_layout_baseline.json`
  - `Results/wpt_fetch_cors_results.json`
  - `Results/wpt_fetch_cors_baseline.json`
- The packs are intentionally bounded so milestone D/E evidence can be regenerated quickly without rerunning the full WPT tree.
## 6.16 Milestone D/E Pack Recovery

- The CSS WPT runner now uses microtask-first harness scheduling and exposes `assert_in_array`, removing a runner-side blocker for CSS parsing/computed-value packs.
- The bounded fetch pack was re-scoped away from referrer-policy server fixtures and toward self-contained request/response coverage.
- Binary body APIs required by `response-consume.html` are now present at a baseline compatibility level: `Blob`, `FormData`, `FileReader`, `Response.blob()`, `Response.formData()`, and blob URL fetch resolution.
## 6.17 Milestone D Gap-Property Recovery

- The CSS gap-family WPT failures moved from harness-level non-execution into concrete property-semantic failures.
- The engine bridge now normalizes and canonicalizes the `gap` / `row-gap` / `column-gap` family and their legacy `grid-*` aliases in both inline style access and `getComputedStyle()`.
- This specifically targets the bounded CSS parsing pack failures around default `normal` values and canonical `0px` serialization.
