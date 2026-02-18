# FenBrowser Deep Source Audit (Windows-First)

Date: 2026-02-18  
Motto alignment: **Architecture is Destiny**

## 1) Scope and Method

- Full repository pass over source and docs, focused on `FenBrowser.FenEngine`, `FenBrowser.Core`, `FenBrowser.Host`, `FenBrowser.DevTools`, `FenBrowser.WebDriver`, and verification assets.
- Audit style: browser-engine architecture review with explicit maturity buckets:
  - `Basic`
  - `Half-built`
  - `Built fully but not wired properly`
  - `Built and wired`
  - cross-cut tags: `Hardcoded`, `Not secure`
- Evidence sources:
  - static scan (`rg`, line-by-line file inspection)
  - existing docs consistency checks
  - build diagnostics (`build_diag_latest.txt`)
- Current codebase size snapshot:
  - `FenBrowser.FenEngine`: 269 files / 105,591 lines
  - `FenBrowser.Core`: 101 files / 21,912 lines
  - `FenBrowser.Host`: 47 files / 10,613 lines
  - `FenBrowser.DevTools`: 29 files / 6,433 lines
  - `FenBrowser.WebDriver`: 22 files / 2,287 lines
  - `FenBrowser.Tests`: 90 files / 10,670 lines
  - `FenBrowser.Test262`: 15 files / 44,690 lines

## 2) Executive Scorecard (1-100)

| Area | Score | Maturity | Notes |
|---|---:|---|---|
| Architecture and modularity | 54 | Built fully but not wired properly | Good layer intent, but parser split and mega-files increase drift risk |
| Parsing and DOM | 64 | Built mostly, partially unwired | Core parser is strong, FenEngine parser divergence is explicitly documented in code |
| CSS and layout | 67 | Built mostly | Breadth is high, but hardcoded debug paths and large complexity hotspots remain |
| Rendering and compositor | 62 | Built mostly | Substantial implementation, but backend abstraction leaks Skia types |
| JavaScript runtime and execution model | 49 | Half-built | Large custom runtime exists, but placeholder runtime and callback bug reduce trust |
| Event loop and phase isolation | 44 | Built fully but not wired properly | Phase transitions are started but not consistently closed |
| Security posture | 36 | Not secure | TLS bypass, exposed remote debug eval surface, weak origin handling patterns |
| DevTools and WebDriver | 38 | Half-built / unwired hardening | Feature surface exists, security controls are incomplete or unused |
| Test and verification quality | 46 | Half-built | Good volume, but WPT runner stub + placeholder assertions |
| Documentation integrity | 33 | Built but stale/inconsistent | Multiple docs overstate readiness vs current source state |
| Build reproducibility (current machine state) | 41 | Basic | Build resolver issues block clean validation in this environment |

**Overall engineering readiness score: 48/100**

## 3) Maturity Buckets Across the Source

### Built and wired (good foundation)
- Core parsing and DOM v2 stack has substantial implementation depth.
- Network/security primitives exist (`CspPolicy`, `SandboxPolicy`, `HstsHandler`, CORS utility checks).
- Layout/rendering breadth is significant (block/inline/flex/grid/paint tree/backends).

### Built fully but not wired properly
- `PipelineContext` APIs are present but not integrated into runtime pipeline flow:
  - `FenBrowser.Core/Engine/PipelineContext.cs:110`
  - `FenBrowser.Core/Engine/PipelineContext.cs:201`
  - `FenBrowser.Core/Engine/PipelineContext.cs:215`
  - `FenBrowser.Core/Engine/PipelineContext.cs:229`
  - Repo usage scan shows no callers outside `PipelineContext.cs`.
- `OriginValidator` exists but is not used by `WebDriverServer` request path:
  - `FenBrowser.WebDriver/Security/OriginValidator.cs:18`
  - `FenBrowser.WebDriver/WebDriverServer.cs:122`
- Event-loop phase boundaries are incomplete (begin without corresponding end in rendering/RAF path):
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:217`
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:233`
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:267`

### Half-built
- WPT runner still declares stub behavior:
  - `FenBrowser.FenEngine/Testing/WPTTestRunner.cs:135`
  - `FenBrowser.FenEngine/Testing/WPTTestRunner.cs:283`
- Service worker fetch dispatch is TODO and returns false:
  - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:143`
  - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:145`
- WebDriver window rect implementation still placeholder:
  - `FenBrowser.Host/WebDriver/FenBrowserDriver.cs:267`

### Basic / stub
- Placeholder browser engine implementation:
  - `FenBrowser.FenEngine/Rendering/BrowserEngine.cs:32`
  - `FenBrowser.FenEngine/Rendering/BrowserEngine.cs:33`
- Placeholder JavaScript runtime wrapper:
  - `FenBrowser.FenEngine/Scripting/JavaScriptRuntime.cs:7`
- Empty class left in production project:
  - `FenBrowser.WebDriver/Class1.cs:3`

### Hardcoded
- Debug logging enabled as constant:
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs:28`
- Absolute local fallback paths:
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs:184`
  - `FenBrowser.Core/Engine/PhaseGuard.cs:319`
- Quantified: 65 hardcoded absolute-path matches across 13 production files.

### Not secure
- TLS validation bypass for image loading:
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:114`
- Remote debug server binds all interfaces and is started on app init:
  - `FenBrowser.DevTools/Core/RemoteDebugServer.cs:43`
  - `FenBrowser.Host/ChromeManager.cs:69`
  - `FenBrowser.Host/ChromeManager.cs:158`
  - `FenBrowser.Host/ChromeManager.cs:159`
- Remote debug runtime can evaluate arbitrary expressions:
  - `FenBrowser.DevTools/Domains/RuntimeDomain.cs:61`
- WebDriver CORS is wildcard and header-check logic is prefix-based:
  - `FenBrowser.WebDriver/WebDriverServer.cs:116`
  - `FenBrowser.WebDriver/WebDriverServer.cs:123`

## 4) Severity-Ranked Issues and How to Fix

## Critical

1. **Image TLS bypass (`Not secure`)**
- Evidence: `FenBrowser.FenEngine/Rendering/ImageLoader.cs:114`
- Problem: Any certificate is accepted, enabling MITM for image content.
- Fix:
  1. Remove unconditional callback in `ImageLoader`.
  2. Reuse centralized `HttpClientFactory` certificate policy.
  3. Add regression test: invalid cert must fail image fetch unless explicit dev flag is enabled.

2. **Remote debug attack surface (`Not secure`)**
- Evidence:
  - `FenBrowser.DevTools/Core/RemoteDebugServer.cs:43`
  - `FenBrowser.DevTools/Domains/RuntimeDomain.cs:61`
  - `FenBrowser.Host/ChromeManager.cs:69`
- Problem: Exposed debug transport + script execution path starts automatically.
- Fix:
  1. Default OFF in release builds.
  2. Bind loopback by default; require explicit `--remote-debug-address`.
  3. Require auth token for all protocol requests.
  4. Gate `Runtime.evaluate` behind authenticated session and allowlist modes.

## High

3. **WebDriver origin/CORS hardening gap (`Not secure`)**
- Evidence:
  - `FenBrowser.WebDriver/WebDriverServer.cs:116`
  - `FenBrowser.WebDriver/WebDriverServer.cs:123`
  - `FenBrowser.WebDriver/Security/OriginValidator.cs:18`
- Problem: Wildcard CORS + naive string prefix checks; dedicated validator is not wired.
- Fix:
  1. Use `OriginValidator.ValidateOriginHeader`.
  2. Replace wildcard CORS with exact local origin echo when validated.
  3. Parse URI host+scheme+port, reject malformed and suffix/prefix tricks.
  4. Add test matrix for localhost variants and malicious lookalikes.

4. **Execution callback duplicate invocation bug**
- Evidence: `FenBrowser.FenEngine/Core/ExecutionContext.cs:39`, `FenBrowser.FenEngine/Core/ExecutionContext.cs:40`
- Problem: `ScheduleCallback` executes the callback twice.
- Fix:
  1. Remove duplicate call.
  2. Route all timers through `EventLoopCoordinator` task queue.
  3. Add unit test asserting exactly-once callback semantics.

5. **Phase closure inconsistency in event loop**
- Evidence:
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:217`
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:233`
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:267`
- Problem: rendering/observer/animation phases are begun without matching closure discipline.
- Fix:
  1. Wrap each `BeginPhase` in `try/finally` with `EndPhase`.
  2. Add guard assertions at end of tick (`EnginePhase.Idle` expected).
  3. Add regression test that phase stack depth returns to zero every tick.

6. **SVG hard limits do not follow project hard constraints**
- Evidence:
  - `FenBrowser.FenEngine/Adapters/ISvgRenderer.cs:108`
  - `FenBrowser.FenEngine/Adapters/ISvgRenderer.cs:109`
  - `FenBrowser.FenEngine/Adapters/ISvgRenderer.cs:110`
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:325`
- Problem: Default limits are `64 / 20 / 2000ms`; AGENTS policy requires `32 / 10 / 100ms`.
- Fix:
  1. Align `SvgRenderLimits.Default` to manifesto limits.
  2. Keep a separate explicit developer override mode.
  3. Add tests to enforce depth/filter/time caps.

## Medium

7. **Hardcoded path and debug-logging pollution**
- Evidence:
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs:28`
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs:184`
  - `FenBrowser.Core/Engine/PhaseGuard.cs:319`
- Problem: Windows-user-specific paths and always-on debug writes reduce portability and reliability.
- Fix:
  1. Replace absolute paths with `LogManager` or configurable app data paths.
  2. Compile-guard debug file writes (`#if DEBUG`).
  3. Centralize diagnostics sink and remove ad-hoc `AppendAllText`.

8. **Resource manager double initialization risk**
- Evidence:
  - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:191`
  - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:301`
- Problem: field initialized, then reassigned in constructor.
- Fix:
  1. Remove eager field initialization.
  2. Construct once in constructor.
  3. Validate disposal ownership in `Dispose`.

9. **Parser split and behavior divergence**
- Evidence:
  - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs:801`
  - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs:804`
- Problem: engine parser and core parser diverged; runtime bypasses one with explicit comment.
- Fix:
  1. Consolidate onto a single parser stack (prefer Core parser).
  2. Decommission duplicate FenEngine HTML parser path or mark experimental only.
  3. Build conformance suite that runs once across one parser entrypoint.

10. **Rendering backend abstraction leak**
- Evidence:
  - `FenBrowser.FenEngine/Rendering/IRenderBackend.cs:1`
  - `FenBrowser.FenEngine/Rendering/IRenderBackend.cs:23`
- Problem: interface comment says backend types should not escape, but `SK*` types are in interface.
- Fix:
  1. Introduce engine-native geometry/paint DTOs.
  2. Keep `SkiaRenderBackend` as adapter.
  3. Migrate call sites incrementally with compatibility shims.

11. **Verification quality gap (runner stub + placeholder asserts)**
- Evidence:
  - `FenBrowser.FenEngine/Testing/WPTTestRunner.cs:135`
  - `FenBrowser.FenEngine/Testing/WPTTestRunner.cs:283`
  - `FenBrowser.Tests/Engine/IntersectionObserverTests.cs:39`
  - `FenBrowser.Tests/Workers/WorkerTests.cs:62`
- Problem: some verification paths can report activity without real assertions.
- Fix:
  1. Implement real headless execution path in WPT runner.
  2. Replace `Assert.True(true)` with observable state assertions.
  3. Fail CI when placeholder tests remain.

12. **Service worker fetch path incomplete**
- Evidence:
  - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:143`
  - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:145`
- Problem: dispatch path is TODO and always false.
- Fix:
  1. Implement dispatch into worker runtime and response propagation.
  2. Add cache/registration integration tests.
  3. Add timeout and fallback semantics.

13. **Entry point duplication and hardcoded harness paths**
- Evidence:
  - `FenBrowser.Host/Program.cs:116`
  - `FenBrowser.Host/Program.cs:272`
  - `FenBrowser.Host/Program.cs:48`
  - `FenBrowser.Host/Program.cs:120`
- Problem: duplicated `--wpt` branch and local-path defaults.
- Fix:
  1. Consolidate CLI routing into single command map.
  2. Move paths to environment/config/arguments only.
  3. Add parser tests for CLI mode behavior.

## Low

14. **Skia resource lifetime risks**
- Evidence:
  - `FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs:484`
  - `FenBrowser.Host/Widgets/InspectorPopupWidget.cs:124`
  - `FenBrowser.Host/Widgets/InspectorPopupWidget.cs:146`
- Problem: new `SKPath` and `SKPaint` allocations are created without deterministic disposal in hot paths.
- Fix:
  1. Dispose prior `_currentPath` before reassignment in `beginPath`.
  2. Use `using` for transient paints and implement widget disposal for persistent paints.
  3. Add leak regression test around repeated paint cycles.

15. **Documentation drift vs actual state**
- Evidence:
  - `docs/JAVASCRIPT_ENGINE_GAP_ANALYSIS.md:89`
  - `docs/TEST262_RESULTS.md:6`
  - `test262_results.md:69`
  - `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md:121`
- Problem: documentation currently overstates readiness and contains inconsistent metrics/file references.
- Fix:
  1. Re-baseline pass rates from latest reproducible run.
  2. Align file names (`WPTTestRunner.cs` vs `WptRunner.cs`).
  3. Add doc update checks in CI for key benchmark tables.

## 5) Quantitative Risk Signals

Static smell counts (production projects):

| Project | TODO/Stub/Placeholder | Hardcoded absolute path hits | `catch (Exception)` | `Console.Write*` |
|---|---:|---:|---:|---:|
| `FenBrowser.FenEngine` | 150 | 62 | 182 | 204 |
| `FenBrowser.Core` | 8 | 1 | 42 | 12 |
| `FenBrowser.Host` | 45 | 2 | 20 | 48 |
| `FenBrowser.WebDriver` | 2 | 0 | 4 | 0 |
| `FenBrowser.DevTools` | 2 | 0 | 22 | 0 |

Largest complexity hotspots:
- `FenBrowser.FenEngine/Core/FenRuntime.cs` (9,540 lines)
- `FenBrowser.FenEngine/Core/Interpreter.cs` (5,304 lines)
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs` (5,081 lines)
- `FenBrowser.FenEngine/Core/Parser.cs` (4,902 lines)
- `FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs` (3,563 lines)

## 6) Comparison With Ladybird (No Engine Adoption)

This comparison is about architecture discipline only, not adopting other engines.

External references:
- Ladybird project overview: https://github.com/LadybirdBrowser/ladybird
- Ladybird project site: https://ladybird.org/

Observed deltas:

1. **Single source of truth**
- Ladybird publicly presents distinct core libraries (LibWeb, LibJS, etc.) with clear ownership boundaries.
- FenBrowser currently has parser duality (`Core.Parsing` vs `FenEngine.HTML`) and runtime fallback comments, increasing behavior drift risk.

2. **Surface hardening before exposure**
- Ladybird style process tends to keep engine internals behind defined layers.
- FenBrowser currently auto-starts remote debug and WebDriver servers during `ChromeManager.Initialize`, with weak/default-open security posture.

3. **Abstraction integrity**
- FenBrowser comments describe backend isolation goals, but `IRenderBackend` still exposes `SK*` types directly.
- This weakens long-term portability objective ("Windows-first, not Windows-forever").

## 7) Remediation Plan (No Compromises)

### Phase 0 (0-3 days): Security lockdown
1. Remove image TLS bypass.
2. Disable remote debug by default; loopback-only + token auth when enabled.
3. Harden WebDriver origin/CORS checks using wired `OriginValidator`.
4. Align SVG hard limits to `32/10/100ms`.

### Phase 1 (4-10 days): Correctness and wiring
1. Fix `ExecutionContext.ScheduleCallback` double invoke.
2. Close all event-loop phases with `try/finally`.
3. Wire `PipelineContext` snapshots into style/layout/paint transitions.
4. Remove duplicate `--wpt` branch and normalize CLI handling.

### Phase 2 (2-5 weeks): Architecture debt burn-down
1. Unify parser entrypoint on one production parser.
2. Split mega-files (`FenRuntime`, `Interpreter`, `CssLoader`) into bounded modules.
3. Remove hardcoded paths and centralize diagnostics configuration.
4. Seal Skia lifetime discipline with disposable ownership model.

### Phase 3 (ongoing): Verification truthfulness
1. Replace placeholder assertions.
2. Complete WPT runner execution path.
3. Re-baseline docs (`TEST262_RESULTS`, compliance claims) from reproducible runs.
4. Add CI policy checks for stale benchmark/document numbers.

## 8) Immediate Priority List

1. `FenBrowser.FenEngine/Rendering/ImageLoader.cs`
2. `FenBrowser.DevTools/Core/RemoteDebugServer.cs`
3. `FenBrowser.DevTools/Domains/RuntimeDomain.cs`
4. `FenBrowser.Host/ChromeManager.cs`
5. `FenBrowser.WebDriver/WebDriverServer.cs`
6. `FenBrowser.FenEngine/Core/ExecutionContext.cs`
7. `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs`
8. `FenBrowser.FenEngine/Adapters/ISvgRenderer.cs`
9. `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
10. `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs`

## 9) Build State Note (Current Environment)

- `dotnet build FenBrowser.sln -c Debug` currently fails in this machine state due workload SDK resolver errors:
  - `build_diag_latest.txt:654`
  - `build_diag_latest.txt:3430`
- Root signal: missing `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator` SDK directory in local dotnet installation.

---

This audit is intentionally strict and architecture-first.  
Target state is not "works on my machine"; target state is durable browser-engine correctness and security.

## 10) Second-Pass Addendum (Missed Findings Added)

This section captures additional issues found in a second full pass.

### Revised Overall Score

- Previous: `48/100`
- Revised after second pass: **45/100**

Reason: additional verified security and architecture-wiring gaps in network pathways, WebDriver enforcement, worker loading, and storage isolation.

### Newly Added High/Critical Findings

16. **Network policy bypass via fragmented ad-hoc HttpClient usage (`Not secure`, `Built but unwired`)**
- Evidence (direct clients outside centralized `ResourceManager` path):
  - `FenBrowser.FenEngine/WebAPIs/XMLHttpRequest.cs:93`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs:136`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1764`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2073`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2343`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2751`
  - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:145`
- Central enforcement path exists but is bypassed:
  - `FenBrowser.Core/ResourceManager.cs:247`
  - `FenBrowser.Core/ResourceManager.cs:933`
- Risk:
  - CSP, consistent cookie/certificate policy, and future tracking-prevention policies can be bypassed by alternate codepaths.
- Fix:
  1. Route all network fetches through one browser-network facade (`ResourceManager`/`NetworkClient`).
  2. Ban direct `new HttpClient()` outside network infrastructure with analyzer/lint rule.
  3. Add regression tests asserting CSP/connect-src enforcement across XHR/fetch/module/script/worker loads.

17. **IndexedDB implementation is overridden by a stub (`Built but unwired`, data isolation risk)**
- Evidence:
  - Functional IndexedDB is registered in runtime:
    - `FenBrowser.FenEngine/Core/FenRuntime.cs:6171`
    - `FenBrowser.FenEngine/Core/FenRuntime.cs:8613`
  - Then a separate stub-style service is registered from JavaScript engine:
    - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:707`
  - Stub service uses static map keyed by db name only:
    - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:13`
    - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:27`
- Risk:
  - API behavior drift and potential cross-origin data mixing if stub path is active.
- Fix:
  1. Remove `IndexedDBService.Register(...)` override in `JavaScriptEngine`.
  2. Keep a single source of truth: `FenRuntime.CreateIndexedDB()`.
  3. Add conformance tests for origin partitioning and upgrade lifecycle callbacks.

18. **WebDriver security component wiring gap (`RESOLVED in Phase-0 continuation, 2026-02-18`)**
- Resolution evidence:
  - Security context bootstrap is now called for session-scoped commands:
    - `FenBrowser.WebDriver/Commands/CommandHandler.cs:55`
    - `FenBrowser.WebDriver/Commands/CommandHandler.cs:161`
  - Navigation/script policies are now enforced before execution:
    - `FenBrowser.WebDriver/Commands/NavigationCommands.cs:47`
    - `FenBrowser.WebDriver/Commands/ScriptCommands.cs:38`
    - `FenBrowser.WebDriver/Commands/ScriptCommands.cs:68`
  - WebDriver startup in host is now opt-in:
    - `FenBrowser.Host/ChromeManager.cs:206`
    - `FenBrowser.Host/ChromeManager.cs:212`
    - `FenBrowser.Host/ChromeManager.cs:218`
- Residual note:
  - `SandboxEnforcer` lifecycle is now wired (create/destroy), but storage/cookie isolation still needs full command-surface integration in later phases.

19. **Worker script loading allows local file reads and bypasses browser network policy (`Not secure`)**
- Evidence:
  - Remote fetch via raw `HttpClient`:
    - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:145`
  - Local file fallback:
    - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:151`
  - Constructor accepts raw script URL without policy gate:
    - `FenBrowser.FenEngine/Workers/WorkerConstructor.cs:42`
    - `FenBrowser.FenEngine/Workers/WorkerConstructor.cs:46`
- Risk:
  - Untrusted script can attempt local-path loading and bypass CSP/network policy integration.
- Fix:
  1. Resolve worker scripts through centralized fetch pipeline.
  2. Enforce same-origin/CORS/CSP worker-src checks.
  3. Block local file fallback unless explicit trusted mode is enabled.

20. **Potential path traversal in persistent IndexedDB backend (`Not secure`)**
- Evidence:
  - Database filename is built from unsanitized JS-controlled name:
    - `FenBrowser.FenEngine/Storage/FileStorageBackend.cs:285`
  - Name originates from script argument:
    - `FenBrowser.FenEngine/Core/FenRuntime.cs:8621`
- Risk:
  - Crafted DB names may attempt path traversal or invalid filesystem paths.
- Fix:
  1. Strictly sanitize database names (allowlist chars + length cap).
  2. Normalize and assert resulting path remains under origin directory.
  3. Add tests for traversal strings (`..`, separators, reserved names).

21. **Synchronous waits across async/UI boundaries (`Built but fragile`)**
- Evidence:
  - DevTools wiring uses blocking waits on dispatched main-thread tasks:
    - `FenBrowser.Host/ChromeManager.cs:198`
    - `FenBrowser.Host/ChromeManager.cs:202`
    - `FenBrowser.Host/ChromeManager.cs:207`
    - `FenBrowser.Host/ChromeManager.cs:219`
  - Script evaluation path blocks with `Task.Wait` and `Result`:
    - `FenBrowser.Host/BrowserIntegration.cs:217`
    - `FenBrowser.Host/BrowserIntegration.cs:218`
  - Worker loop busy-waits with `Thread.Sleep`:
    - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:193`
- Risk:
  - Deadlock/perf jitter risks under contention and reduced responsiveness under load.
- Fix:
  1. Convert blocking calls to end-to-end async pipelines.
  2. Replace busy sleep with wait handle / queue signaling.
  3. Add stress tests for DevTools-heavy and worker-heavy scenarios.

### Additional Quantified Signal

- `new HttpClient(...)` call sites across production code: `11` total.
- Only one is in the centralized factory path:
  - `FenBrowser.Core/Network/HttpClientFactory.cs:77`
- The remaining ad-hoc call sites should be treated as migration debt.

### Updated Priority Insertions

Add these to immediate remediation queue:

11. `FenBrowser.FenEngine/WebAPIs/XMLHttpRequest.cs`  
12. `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`  
13. `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs`  
14. `FenBrowser.FenEngine/Workers/WorkerRuntime.cs`  
15. `FenBrowser.FenEngine/Workers/WorkerConstructor.cs`  
16. `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs`  
17. `FenBrowser.FenEngine/Storage/FileStorageBackend.cs`  
18. `FenBrowser.WebDriver/Commands/CommandHandler.cs`  
19. `FenBrowser.WebDriver/Commands/NavigationCommands.cs`  
20. `FenBrowser.WebDriver/Commands/ScriptCommands.cs`

## 11) Third-Pass Addendum (Production-Browser Gaps)

### Revised Overall Score (after third pass)

- Previous: `45/100`
- Revised: **41/100**

Reason: additional confirmed gaps in process model, cookie/storage coherence, protocol coverage, settings wiring, and navigation hardening.

### Newly Added Findings

22. **No process isolation model (thread-only isolation) (`Architecture risk`, `Not secure`)**
- Evidence:
  - Engine is started as an in-process thread:
    - `FenBrowser.Host/BrowserIntegration.cs:141`
  - Worker runtime also runs via thread:
    - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:64`
- Finding:
  - Current isolation is thread-based in one process; there is no broker/renderer split or site/process isolation boundary.
- Fix:
  1. Define a minimal multi-process model (Browser process + Renderer process at minimum).
  2. Move untrusted web execution and parsing/rendering to constrained renderer process.
  3. Add IPC contracts for navigation, input, paint, and resource mediation.

23. **Cookie model is fragmented across multiple independent stores (`Built but unwired`, `Security/correctness risk`)**
- Evidence:
  - Engine cookie jar:
    - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs:1336`
  - DOM wrapper local cookie dictionary (attributes ignored):
    - `FenBrowser.FenEngine/DOM/DocumentWrapper.cs:642`
    - `FenBrowser.FenEngine/DOM/DocumentWrapper.cs:654`
  - BrowserHost WebDriver cookie dictionary:
    - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:1908`
  - DevTools cookie list:
    - `FenBrowser.FenEngine/DevTools/DevToolsCore.cs:752`
  - WebDriver sandbox cookie store:
    - `FenBrowser.WebDriver/Security/SandboxEnforcer.cs:74`
- Finding:
  - There is no single source-of-truth cookie store; flags like `Secure/HttpOnly/SameSite` are not consistently enforced end-to-end.
- Fix:
  1. Create one canonical cookie store service in core network layer.
  2. Route DOM, DevTools, WebDriver, and engine reads/writes through that service.
  3. Enforce domain/path/expiry/SameSite/Secure/HttpOnly semantics in one place.

24. **Storage model fragmentation and privacy-mode inconsistency (`Built but unwired`)**
- Evidence:
  - `StorageApi` uses static global persistence path:
    - `FenBrowser.FenEngine/WebAPIs/StorageApi.cs:21`
  - `sessionStorage` backing ignores origin key:
    - `FenBrowser.FenEngine/WebAPIs/StorageApi.cs:121`
  - Separate JS-engine local/session maps also exist:
    - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1043`
  - Private mode exists on browser host:
    - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:256`
  - `ClearBrowsingData` clears cookies/cache but not storage:
    - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:945`
    - `FenBrowser.FenEngine/Rendering/BrowserApi.cs:950`
- Finding:
  - Storage behavior is split across multiple implementations and does not clearly respect private-mode semantics end-to-end.
- Fix:
  1. Consolidate localStorage/sessionStorage/IndexedDB persistence behind one storage coordinator.
  2. Ensure private sessions use non-persistent storage and guaranteed teardown.
  3. Include storage clearing in browsing-data clear flow.

25. **WebDriver protocol surface is much larger than actual implementation (`Half-built`)**
- Evidence:
  - Routes declared for cookies/actions/alerts/etc:
    - `FenBrowser.WebDriver/CommandRouter.cs:95`
  - Command switch handles a smaller subset and defaults unsupported:
    - `FenBrowser.WebDriver/Commands/CommandHandler.cs:56`
    - `FenBrowser.WebDriver/Commands/CommandHandler.cs:98`
  - Measured counts from source:
    - `RouteCommands=58`
    - `HandledCommands=25`
    - `MissingCount=33`
- Fix:
  1. Implement missing high-priority WebDriver commands in spec order.
  2. Publish coverage matrix in docs with pass/fail per command.
  3. Add protocol conformance tests for route-to-handler parity.

26. **Settings policy wiring is incomplete (UI toggles not enforced in runtime) (`Built but unwired`)**
- Evidence:
  - `SendDoNotTrack` setting exists:
    - `FenBrowser.Core/BrowserSettings.cs:121`
  - Privacy handler always sets DNT header regardless setting:
    - `FenBrowser.Core/Network/Handlers/PrivacyHandler.cs:14`
    - `FenBrowser.Core/Network/Handlers/PrivacyHandler.cs:16`
  - `UseSecureDNS` setting is only in UI/settings storage:
    - `FenBrowser.Core/BrowserSettings.cs:124`
    - `FenBrowser.Host/Widgets/SettingsPageWidget.cs:227`
  - `SafeBrowsing`, `ImproveBrowser`, `BlockPopups` similarly remain mostly UI-config only.
- Fix:
  1. Add explicit runtime policy bindings for each user-facing privacy/security toggle.
  2. Remove or hide toggles that are not functionally wired.
  3. Add startup diagnostics logging showing active policy settings actually enforced.

27. **Navigation hardening still permissive for local-file auto conversion (`Not secure` in hostile contexts)**
- Evidence:
  - Rooted local paths are converted to `file:///...`:
    - `FenBrowser.FenEngine/Rendering/NavigationManager.cs:35`
    - `FenBrowser.FenEngine/Rendering/NavigationManager.cs:40`
  - Multiple schemes permitted in normalization path:
    - `FenBrowser.FenEngine/Rendering/NavigationManager.cs:46`
- Finding:
  - In automation/untrusted inputs, local-path auto-conversion increases risk without a strict capability gate.
- Fix:
  1. Gate `file://` navigation behind explicit policy/capability.
  2. Separate user-typed URL normalization from programmatic navigation policy.
  3. Add deny-by-default mode for automation contexts.

28. **CSP `'self'` evaluation path is under-specified in caller usage (`Correctness/spec gap`)**
- Evidence:
  - `'self'` requires non-null origin:
    - `FenBrowser.Core/Security/CspPolicy.cs:138`
  - Key callers pass only `(directive, url)` without origin:
    - `FenBrowser.Core/ResourceManager.cs:247`
    - `FenBrowser.Core/ResourceManager.cs:933`
- Finding:
  - This can lead to incorrect `'self'` decisions (over-blocking or inconsistent behavior) depending on directive/source list.
- Fix:
  1. Thread document/request origin through CSP checks.
  2. Add CSP unit tests specifically for `'self'`, subdomains, and port handling.
  3. Keep one mandatory call signature that includes origin context.

29. **ES module loader default path is file-only (`Half-built`)**
- Evidence:
  - Default module loader fetcher only supports `file://`:
    - `FenBrowser.FenEngine/Core/ModuleLoader.cs:23`
    - `FenBrowser.FenEngine/Core/ModuleLoader.cs:26`
- Fix:
  1. Integrate module fetch with centralized network/CSP/CORS pipeline.
  2. Add proper module-map caching and import resolution for http(s).
  3. Add module security checks aligned with script policy.

### Updated Priority Insertions (Third Pass)

21. `FenBrowser.Core/Network/Handlers/PrivacyHandler.cs`  
22. `FenBrowser.FenEngine/WebAPIs/StorageApi.cs`  
23. `FenBrowser.FenEngine/DOM/DocumentWrapper.cs`  
24. `FenBrowser.FenEngine/DevTools/DevToolsCore.cs`  
25. `FenBrowser.FenEngine/Rendering/NavigationManager.cs`  
26. `FenBrowser.WebDriver/CommandRouter.cs`  
27. `FenBrowser.WebDriver/Commands/CommandHandler.cs`  
28. `FenBrowser.Host/BrowserIntegration.cs`  
29. `FenBrowser.FenEngine/Core/ModuleLoader.cs`

## 12) Phase-0 Execution Log (Started 2026-02-18)

Implemented now:

1. **Image TLS bypass removed**
- `FenBrowser.FenEngine/Rendering/ImageLoader.cs`

2. **SVG hard limits aligned to policy (`32/10/100`)**
- `FenBrowser.FenEngine/Adapters/ISvgRenderer.cs`

3. **Remote debug hardened**
- Added loopback-first bind + optional auth token support:
  - `FenBrowser.DevTools/Core/RemoteDebugServer.cs`
- Disabled by default in host startup; explicit opt-in via env vars:
  - `FenBrowser.Host/ChromeManager.cs`

4. **WebDriver origin/CORS hardened**
- Integrated `OriginValidator` into request ingress:
  - `FenBrowser.WebDriver/WebDriverServer.cs`
- Tightened header validation rules:
  - `FenBrowser.WebDriver/Security/OriginValidator.cs`

5. **WebDriver command-path and startup hardening completed**
- Wired security enforcement into command execution:
  - `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  - `FenBrowser.WebDriver/Commands/NavigationCommands.cs`
  - `FenBrowser.WebDriver/Commands/ScriptCommands.cs`
- Host startup policy changed to explicit opt-in:
  - `FenBrowser.Host/ChromeManager.cs`
  - Env flags:
    - `FEN_WEBDRIVER=1`
    - `FEN_WEBDRIVER_PORT` (optional, default `4444`)

## 13) Phase-1 Execution Log (Started 2026-02-18)

Implemented now:

1. **`ExecutionContext.ScheduleCallback` exactly-once fix**
- Removed duplicate callback invocation in default scheduler:
  - `FenBrowser.FenEngine/Core/ExecutionContext.cs`

2. **Event-loop phase closure hardening (`try/finally`)**
- Added explicit phase closure in task/layout/observer/RAF callback paths:
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs`
- Added idle-phase leak recovery guard:
  - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs`

3. **`PipelineContext` snapshot wiring into style/layout/paint transitions**
- Added frame lifecycle and stage snapshot updates in renderer:
  - `FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs`
- Wired stage dirty-flag propagation via `PipelineContext.DirtyFlags`.

4. **CLI normalization for WPT execution**
- Removed duplicate `--wpt` branch and retained a single authoritative path:
  - `FenBrowser.Host/Program.cs`

Validation note:
- `dotnet build` remains blocked by pre-existing local SDK/workload resolver state in this machine (no project diagnostics emitted).

## 14) Phase-2 Execution Log (Started 2026-02-18)

Implemented now:

1. **Centralized diagnostics path resolver**
- Added reusable path abstraction for debug artifacts and logs:
  - `FenBrowser.Core/Logging/DiagnosticPaths.cs`
- Supports environment override:
  - `FEN_DIAGNOSTICS_DIR`

2. **Removed hardcoded absolute diagnostic file paths in core engine guards**
- Replaced machine-specific path in:
  - `FenBrowser.Core/Engine/PhaseGuard.cs`

3. **Removed hardcoded absolute diagnostic file paths in rendering/layout hot paths**
- Replaced direct absolute debug writes with centralized helpers in:
  - `FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs`
  - `FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs`
  - `FenBrowser.FenEngine/Layout/LayoutEngine.cs`
  - `FenBrowser.FenEngine/Rendering/SkiaRenderer.cs`

4. **Removed hardcoded script execution log path**
- Centralized script execution log target in:
  - `FenBrowser.FenEngine/Core/FenRuntime.cs`

5. **Removed hardcoded default harness roots**
- Host and engine defaults now derive from current working directory instead of user-specific absolute paths:
  - `FenBrowser.Host/Program.cs`
  - `FenBrowser.FenEngine/Program.cs`

6. **Completed residual diagnostics path migration (tranche B)**
- Replaced remaining hardcoded diagnostics path callsites and routed them through centralized helpers in:
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`
  - `FenBrowser.FenEngine/WebAPIs/FetchApi.cs`
  - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs`
  - `FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs`
- Removed remaining source-level absolute path literals (`C:\Users\...`) from production code in `FenBrowser.Core`, `FenBrowser.FenEngine`, and `FenBrowser.Host`.
- Added compile-time debug-only gating for CSS file diagnostics in `CssLoader` to prevent release log pollution.

Validation update:
- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Debug` succeeded.
- `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug` succeeded (warnings only, zero errors).
- `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug` remains blocked on this machine with pre-existing resolver state (0 warnings / 0 errors emitted).
