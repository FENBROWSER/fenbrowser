# FenBrowser Codex - Volume VI: Extensions & Verification

**State as of:** 2026-02-06
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

### 3.2 Compliance Runners (`FenBrowser.FenEngine.Testing`)

Specialized runners built to execute standard web test suites against the engine.

- **Test262Runner**: Runs the official ECMA-262 (JavaScript) conformance suite.
- **WPTTestRunner**: Runs the Web Platform Tests (WPT) for DOM/CSS.
- **AcidTestRunner**: Specific runner for the Acid2 layout test, verifying standard rendering compliance.

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

### 4.2 Compliance Verification (`FenBrowser.FenEngine.Testing`)

#### `Test262Runner.cs`

The ECMA-262 (JavaScript) conformance test runner.

- **Lines 92-213**: **`RunSingleTestAsync`**: Orchestrates a single test case: Parsing YAML metadata, executing JS, and validating results against expected outcomes.

#### `WptRunner.cs`

The Web Platform Tests (WPT) runner for DOM/CSS compliance.

- Automates the execution of `.html` tests and compares rendered output or computed styles against reference expectations.

#### `AcidTestRunner.cs`

Specialized harness for the Acid2/Acid3 verification suites.

_End of Volume VI_

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
