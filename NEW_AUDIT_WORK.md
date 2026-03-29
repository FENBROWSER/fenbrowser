# NEW_AUDIT_WORK

## Purpose
This document is derived from `NEW_AUDIT.md`.

It is not a summary. It is the production-grade work ledger for every area that was not rated `production-grade`, every missing seam, and every place where the current architecture is too loose for a browser that intends to serve millions of users.

## Status
P0 was implemented on `2026-03-29`.

The `7` P0 workstreams are now closed in code and synchronized into the volume docs.
Remaining work in this ledger is P1 and P2 completion, plus warning-debt cleanup discovered during solution builds.

### 2026-03-29 P1 Progress Snapshot

- Advanced `Complete Core DOM support surfaces` with concrete invariants for tree-scope ownership, traversal/root boundaries, shadow-root state, sanitization logging, and serializer fidelity.
- Advanced `Harden engine contracts and lifecycle invariants` with a shared phase-transition matrix, scoped phase restoration, pipeline metadata validation, navigation pending-load snapshots, normalized `BrowserSettings`, and explicit browser/network async contracts.
- Advanced `Close WebIDL pipeline end to end` with deterministic generator ordering, manifest hashing, stale-output cleanup, and `--verify` mode in `FenBrowser.WebIdlGen`.
- Advanced `Complete DevTools` with stricter protocol error handling, duplicate-handler rejection, idempotent event subscription, and disposable DOM mutation instrumentation.
- Advanced `Complete WebDriver` with stricter capability validation, request-body validation, route normalization, reference-safe script argument/result conversion, and session/timeout hardening.
- Verification on `2026-03-29`:
  - `dotnet build FenBrowser.sln -nologo`: pass.
  - Focused regression slice for DOM/engine/UI-dispatch/WebIDL/WebDriver/DevTools: pass (`54/54`).
  - Required runtime host cycle emitted `debug_screenshot.png`, `dom_dump.txt`, and `logs/click_debug.log`.
- Open production blocker discovered during the same runtime cycle:
  - `debug_screenshot.png` is still effectively blank/white while `dom_dump.txt` contains a populated Google DOM tree.
  - `logs/raw_source_*.html` still did not emit.
  - P1 therefore remains in progress; the browser still has rendering/diagnostic closure work before P1 can be called fully complete.

### 2026-03-30 P1 Progress Snapshot

- Closed the blank-first-frame regression in the renderer watchdog path:
  - if raster is over budget and no reusable base frame exists, fen now forces a full raster instead of presenting a white frame.
  - if a caller explicitly provides a reusable base frame, fen preserves that seeded frame instead of clearing over it.
- Strengthened render regression coverage:
  - `FenBrowser.Tests/Rendering/RenderWatchdogTests.cs` now covers watchdog trigger, forced full-raster fallback without a base frame, and seeded-base-frame preservation.
  - `FenBrowser.Tests/Core/GoogleSnapshotDiagnosticsTests.cs` now records watchdog state in failure diagnostics while asserting visible raster coverage for the Google search shell.
- Verification on `2026-03-30`:
  - `dotnet build FenBrowser.sln -nologo`: pass.
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -nologo --filter "FullyQualifiedName~RenderWatchdogTests"`: pass (`3/3`).
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -nologo --filter "FullyQualifiedName~GoogleSnapshotDiagnosticsTests.LatestGoogleSnapshot_MainSearchChrome_HasLayoutAndPaintCoverage"`: pass (`1/1`).
  - Required runtime host cycle now emits a visibly painted `debug_screenshot.png` with the Google homepage shell, doodle, buttons, language strip, and footer instead of an all-white frame.
- Remaining P1 diagnostic gap after the render fix:
  - the debug run still emitted `network_fetch_*.html` but not a fresh `raw_source_*.html` or `engine_source_*.html` artifact in the active Debug log directory.
  - the active `fenbrowser_*.log` file remained extremely thin, so first-class structured diagnostics are still not fully surfaced on this path.
  - P1 therefore remains in progress until the runtime diagnostic contract is as reliable as the render path itself.

All work below is guided by the FenBrowser mandate:
- Security is first-class.
- Modularity is first-class.
- Spec compliance is first-class.
- Performance is first-class.
- Logging and diagnostics are first-class.
- We do not repeat the structural mistakes of Chromium, Firefox, or WebKit.
- Architecture is destiny.

## Non-Negotiable Production Rules
- No empty runtime files in shipped code. `stub` means implement or delete.
- No duplicate subsystem ownership. One parser stack, one renderer surface, one source of truth per responsibility.
- No product-runtime and test-harness mixing. Harness code belongs in tools or dedicated runner projects.
- No silent fallback behavior without explicit logging, metrics, and documented intent.
- No large subsystem may ship without a clear owner boundary, stable contracts, failure handling, and structured diagnostics.
- No security-sensitive surface may ship without threat modeling, denial paths, and observable policy enforcement.
- No performance-sensitive hot path may ship without allocation discipline, frame-budget awareness, and traceable telemetry.
- No logging surface may remain ad hoc. Production logging must be structured, categorized, filterable, and cheap on hot paths.
- No library is allowed to dictate browser architecture. External libraries are implementation details, not system owners.
- No hidden global state expansion. Shared state must remain explicit and bounded.

## Closure Rules
- `stub`
  Action: implement immediately or delete immediately.
- `basic`
  Action: keep thin if that is the right shape, but freeze the contract, validate inputs, add invariants, add diagnostics, and remove ambiguity.
- `partial`
  Action: finish the missing semantics, error handling, spec alignment, performance posture, and observability.
- `production-grade`
  Action: keep stable, but still subject to architecture cleanup if it depends on non-production-grade neighbors.

Production-grade does not mean every file becomes large.
Some files should remain small forever.
The requirement is deliberate completeness, not code volume.

## Required Acceptance Gates For Every Work Item
- Security: explicit boundary, deny path, abuse case handling.
- Modularity: single owner, no duplicate authority, clean interfaces.
- Spec: spec source or explicit deviation note.
- Performance: hot-path allocations understood, budget or metric defined.
- Logging: structured logs, categories, and useful failure diagnostics.
- Failure mode: timeout, crash, cancellation, and recovery behavior defined.
- Cleanup: no placeholder comments, no transitional debt without a dated removal plan.

## Anti-Mistakes We Must Avoid
- No parser duplication.
- No renderer adapter layer becoming permanent architecture.
- No "temporary" compatibility surface that silently becomes core.
- No harness code living forever inside runtime projects.
- No claiming full process isolation while key IPC/process seams are empty.
- No platform abstraction that is only nominal on one OS.
- No logging that is either too noisy to use or too weak to debug production incidents.
- No spec drift hidden behind permissive recovery logic.

## Execution Order
1. P0: architecture and safety blockers.
2. P1: subsystem completion and closure.
3. P2: hardening, packaging, and disciplined thin-surface cleanup.

## P0 Workstreams
| Priority | Workstream | Scope From `NEW_AUDIT` | Production-Grade Outcome |
|---|---|---|---|
| P0 | Eliminate stubs and dead scaffold | `FenBrowser.Host\ProcessIsolation\Gpu\GpuProcessIpc.cs`, `FenBrowser.Host\ProcessIsolation\Utility\UtilityProcessIpc.cs`, `FenBrowser.WebDriver\Class1.cs` | No empty runtime seams, no dead scaffold, no placeholder process boundary. IPC either exists with authenticated/versioned messages and telemetry, or the surface is removed. |
| P0 | Separate runtime from harness and audit-only tooling | `FenBrowser.Host\Program.cs`, `FenBrowser.FenEngine\Program.cs`, `FenBrowser.FenEngine\Testing\*`, `FenBrowser.FenEngine\WebAPIs\TestHarnessAPI.cs`, `FenBrowser.FenEngine\WebAPIs\TestConsoleCapture.cs`, WPT/Test262 hooks currently embedded in runtime surfaces | Product runtime projects stop owning test-runner behavior. Browser startup, automation, conformance runners, and internal diagnostics become cleanly separated tools or dedicated projects. |
| P0 | Unify HTML parsing ownership | `FenBrowser.Core\Parsing\*`, `FenBrowser.FenEngine\HTML\*` | One canonical HTML tokenizer/tree-builder stack. The non-canonical stack is removed or reduced to adapters only during a time-boxed migration. No duplicate parser behavior or split bug-fix ownership. |
| P0 | Finalize renderer architecture | `FenBrowser.FenEngine\Rendering\SkiaDomRenderer.cs`, `FenBrowser.FenEngine\Rendering\BrowserApi.cs`, `FenBrowser.FenEngine\Rendering\BrowserEngine.cs`, `FenBrowser.FenEngine\Rendering\Core\*`, `FenBrowser.FenEngine\Rendering\Compositing\*`, `FenBrowser.FenEngine\Rendering\Paint\*`, `FenBrowser.FenEngine\Rendering\PaintTree\*`, `FenBrowser.FenEngine\Rendering\RenderTree\*`, `FenBrowser.FenEngine\Rendering\IRenderBackend.cs` | Remove the "transitional" architecture and define the final renderer boundaries: layout input, paint tree, compositing, raster, damage, overlays, and backend abstraction. No permanent adapter debt. |
| P0 | Accessibility platform parity | `FenBrowser.Core\Accessibility\*` | Windows, Linux, and future macOS paths must have real event emission, stable tree invalidation, platform mapping, and observable failures. Stub-mode AT-SPI is not acceptable for production positioning. |
| P0 | Logging as first-class architecture | `FenBrowser.Core\FenLogger.cs`, `FenBrowser.Core\ConsoleLogger.cs`, `FenBrowser.Core\Logging\*`, `FenBrowser.DevTools\Core\RemoteDebugServer.cs`, high-signal runtime boundaries in Host/FenEngine/WebDriver | Structured logging schema, stable categories, cheap hot-path logging, log shipping and rotation policy, and production incident correlation across browser, engine, process isolation, automation, and DevTools. |
| P0 | Security, sandbox, and policy closure | `FenBrowser.Core\Security\*`, `FenBrowser.Core\Security\Sandbox\*`, `FenBrowser.Core\Platform\*`, `FenBrowser.Core\Interop\Windows\*`, `FenBrowser.Core\Network\*`, `FenBrowser.Core\Network\Handlers\*`, `FenBrowser.WebDriver\Security\*` | Real OS and process isolation posture, explicit policy enforcement, observable denial decisions, hardened origin/CSP/network behavior, and no misleading null-sandbox story for production profiles. |

## P1 Workstreams
| Priority | Workstream | Scope From `NEW_AUDIT` | Production-Grade Outcome |
|---|---|---|---|
| P1 | Complete Core DOM support surfaces | `FenBrowser.Core\Dom\V2\Attr.cs`, `Comment.cs`, `DocumentFragment.cs`, `DocumentType.cs`, `DomException.cs`, `DomExtensions.cs`, `Mixins.cs`, `NodeFlags.cs`, `NodeIterator.cs`, `PseudoElement.cs`, `Security\AttributeSanitizer.cs`, `ShadowRoot.cs`, `Text.cs`, `TreeScope.cs`, `TreeWalker.cs`, `FenBrowser.Core\DomSerializer.cs` | Support files stop being "narrow but loose" and become deliberate browser contracts with stable semantics, invariants, sanitization rules, and serialization guarantees. |
| P1 | Harden engine contracts and lifecycle invariants | `FenBrowser.Core\Engine\EngineContext.cs`, `EngineInvariants.cs`, `EnginePhase.cs`, `NavigationSubresourceTracker.cs`, `PipelineStage.cs`, plus supporting contracts such as `FenBrowser.Core\IBrowserEngine.cs`, `ILogger.cs`, `INetworkService.cs`, `UiThreadHelper.cs`, `BrowserSettings.cs`, `NetworkConfiguration.cs`, `NetworkService.cs` | The engine lifecycle becomes explicit, enforceable, and observable. Contracts become narrow and final instead of permissive glue. |
| P1 | Close WebIDL pipeline end to end | `FenBrowser.Core\WebIDL\WebIdlBindingGenerator.cs`, `FenBrowser.Core\WebIDL\Idl\*`, `FenBrowser.FenEngine\Bindings\Generated\*`, `FenBrowser.WebIdlGen\*` | Generated breadth must map to real runtime semantics. Deterministic generation, validation, and ownership of host-object behavior replace "generated surface implies completeness." |
| P1 | Finish bytecode/runtime support layers | `FenBrowser.FenEngine\Core\Bytecode\*`, `FenBrowser.FenEngine\Core\EventLoop\*`, `FenBrowser.FenEngine\Core\Interfaces\*`, `FenBrowser.FenEngine\Core\Types\JsMap.cs`, `JsWeakMap.cs`, `JsWeakSet.cs`, `ModuleNamespaceObject.cs`, `Shape.cs` | Runtime contracts become final, execution semantics become auditably correct, and the bytecode/event-loop subsystems stop depending on loose helper boundaries. |
| P1 | Complete CSS engine support surfaces | `FenBrowser.Core\Css\CssCornerRadius.cs`, `FenBrowser.Core\Css\ICssEngine.cs`, `FenBrowser.FenEngine\Rendering\Css\*` non-production-grade files, especially `CssLoader.cs`, `CssParser.cs`, `CssSelectorParser.cs`, `CssStyleApplicator.cs`, value/token/model/parser helpers | Styling becomes single-owner, spec-driven, and internally consistent. The "lean parser utility" and older parser removal story must resolve into a coherent CSS subsystem. |
| P1 | Finish layout support layers | `FenBrowser.FenEngine\Layout\*` non-production-grade files, including `AbsolutePositionSolver.cs`, `BoxModel.cs`, `ContainingBlockResolver.cs`, `Contexts\*`, `Coordinates\*`, `GridLayoutComputer.*`, `LayoutContext.cs`, `LayoutEngine.cs`, `LayoutHelper.cs`, `LayoutPositioningLogic.cs`, `LayoutResult.cs`, `LayoutValidator.cs`, `MarginCollapse*`, `MultiColumnLayoutComputer.cs`, `PseudoBox.cs`, `ReplacedElementSizing.cs`, `ScrollAnchoring.cs`, `TextLayout*`, `TransformParsed.cs`, `Tree\*` | Layout helpers become a coherent system with clear ownership, fewer ambiguous utility layers, and validated algorithm boundaries for block, inline, grid, float, absolute, multicolumn, and writing-mode behavior. |
| P1 | Productionize Web APIs and workers | `FenBrowser.FenEngine\WebAPIs\FetchEvent.cs`, `IntersectionObserverAPI.cs`, `ResizeObserverAPI.cs`, `TestConsoleCapture.cs`, `FenBrowser.FenEngine\Workers\*` partial files including `ServiceWorker*.cs`, `StructuredClone.cs`, `WorkerConstructor.cs`, `WorkerPromise.cs` | Observer, fetch-event, structured clone, worker, and service-worker behavior become fully specified, observable, and bounded by real resource and security rules. |
| P1 | Harden typography and shaping | `FenBrowser.FenEngine\Typography\GlyphRun.cs`, `IFontService.cs`, `NormalizedFontMetrics.cs`, `SkiaFontService.cs`, `TextShaper.cs` | Text shaping, metrics, and font selection become reliable, measurable, and backend-independent enough for production rendering quality. |
| P1 | Harden Host shell and interaction layers | `FenBrowser.Host\ChromeManager.cs`, `RootWidget.cs`, `Context\*`, `Input\*`, `Tabs\*`, `Theme\ThemeManager.cs`, non-production-grade `Widgets\*`, plus packaging surfaces `app.manifest`, `FenBrowser.Host.csproj` | The host shell becomes a clear UI layer with strict thread ownership, input determinism, widget contracts, and fewer broad coordinator classes. |
| P1 | Complete DevTools | `FenBrowser.DevTools\Core\*` non-production-grade files, `Domains\*` non-production-grade files, `Domains\DTOs\*`, `Instrumentation\DomInstrumenter.cs`, `Panels\ElementsPanel.cs`, `FenBrowser.DevTools.csproj` | Protocol routing, DTO fidelity, panel behavior, instrumentation, and remote debugging become production service tooling instead of a powerful but partly simplified custom stack. |
| P1 | Complete WebDriver | `FenBrowser.WebDriver\CommandRouter.cs`, `Commands\NavigationCommands.cs`, `ScriptCommands.cs`, `SessionCommands.cs`, `SessionManager.cs`, `Protocol\*`, `Security\*`, `FenBrowser.WebDriver.csproj` | WebDriver becomes fully intentional: spec-aligned command behavior, hardened automation security, stable session semantics, and strong protocol diagnostics. |

## P2 Workstreams
| Priority | Workstream | Scope From `NEW_AUDIT` | Production-Grade Outcome |
|---|---|---|---|
| P2 | Harden thin value/config/contract files without bloating them | `FenBrowser.Core\CertificateInfo.cs`, `FenBrowser.Core\Math\CornerRadius.cs`, `Thickness.cs`, `FenBrowser.Core\Cache\CacheKey.cs`, `ShardedCache.cs`, small interfaces and enums across Core/FenEngine/Host/DevTools/WebDriver | Thin files remain thin, but their APIs become frozen, validated, observable, and intentionally documented. Production-grade does not mean "make this big." It means "make this final." |
| P2 | Packaging and deterministic tooling cleanup | `FenBrowser.Host\app.manifest`, `FenBrowser.Host\icon.ico`, project files across Host/DevTools/WebDriver/WebIdlGen, small platform/config surfaces | Packaging, manifests, and build metadata become deterministic and explicit. No hidden behavior, machine-specific drift, or accidental feature activation. |

## Coverage Map
All non-`production-grade` findings in `NEW_AUDIT.md` are covered by the workstreams above. The coverage rule is by ownership boundary, not by arbitrary file count.

### FenBrowser.Core Coverage
- Accessibility productionization:
  `FenBrowser.Core\Accessibility\*`
- DOM support and sanitization completion:
  `FenBrowser.Core\Dom\V2\Attr.cs`, `Comment.cs`, `DocumentFragment.cs`, `DocumentType.cs`, `DomException.cs`, `DomExtensions.cs`, `Mixins.cs`, `NodeFlags.cs`, `NodeIterator.cs`, `PseudoElement.cs`, `Security\AttributeSanitizer.cs`, `ShadowRoot.cs`, `Text.cs`, `TreeScope.cs`, `TreeWalker.cs`, `FenBrowser.Core\DomSerializer.cs`
- Engine contract hardening:
  `FenBrowser.Core\Engine\EngineContext.cs`, `EngineInvariants.cs`, `EnginePhase.cs`, `NavigationSubresourceTracker.cs`, `PipelineStage.cs`
- Logging first-class:
  `FenBrowser.Core\FenLogger.cs`, `FenBrowser.Core\ConsoleLogger.cs`, `FenBrowser.Core\Logging\*`
- Network and policy hardening:
  `FenBrowser.Core\Network\*`, `FenBrowser.Core\Network\Handlers\*`, `FenBrowser.Core\NetworkConfiguration.cs`, `FenBrowser.Core\NetworkService.cs`
- Platform and sandbox closure:
  `FenBrowser.Core\Platform\*`, `FenBrowser.Core\Security\*`, `FenBrowser.Core\Security\Sandbox\*`, `FenBrowser.Core\Interop\Windows\*`, `FenBrowser.Core\SandboxPolicy.cs`
- Parsing support completion:
  `FenBrowser.Core\Parsing\HtmlParser.cs`, `HtmlToken.cs`, `ICssParser.cs`, `IHtmlParser.cs`, `ParserSecurityPolicy.cs`, `PreloadScanner.cs`
- CSS/runtime support thin-surface hardening:
  `FenBrowser.Core\Css\CssCornerRadius.cs`, `ICssEngine.cs`, `BrowserSettings.cs`, `CertificateInfo.cs`, `Cache\*`, `Math\*`, `IBrowserEngine.cs`, `ILogger.cs`, `INetworkService.cs`, `UiThreadHelper.cs`, `Verification\ContentVerifier.cs`
- WebIDL pipeline:
  `FenBrowser.Core\WebIDL\WebIdlBindingGenerator.cs`, `FenBrowser.Core\WebIDL\Idl\*`

### FenBrowser.FenEngine Coverage
- Parser ownership resolution:
  `FenBrowser.FenEngine\HTML\*`
- Runtime support completion:
  `FenBrowser.FenEngine\Core\Bytecode\*`, `FenBrowser.FenEngine\Core\EventLoop\*`, `FenBrowser.FenEngine\Core\Interfaces\*`, `FenBrowser.FenEngine\Core\Types\*` non-production-grade files
- Generated binding closure:
  `FenBrowser.FenEngine\Bindings\Generated\*`
- Layout completion:
  `FenBrowser.FenEngine\Layout\*` non-production-grade files
- Rendering architecture finalization:
  `FenBrowser.FenEngine\Rendering\*` non-production-grade files, including `SkiaDomRenderer.cs`, `BrowserApi.cs`, `Css\*`, `Compositing\*`, `Core\*`, `Interaction\*`, `Paint\*`, `Painting\*`, `PaintTree\*`, `RenderTree\*`, `WebGL\*`, `UserAgent\*`
- Typography hardening:
  `FenBrowser.FenEngine\Typography\*`
- Web APIs and workers:
  `FenBrowser.FenEngine\WebAPIs\*` non-production-grade files, `FenBrowser.FenEngine\Workers\*` non-production-grade files
- Harness extraction:
  `FenBrowser.FenEngine\Program.cs`, `FenBrowser.FenEngine\Testing\*`

### FenBrowser.Host Coverage
- Startup surface cleanup:
  `FenBrowser.Host\Program.cs`
- Process isolation implementation:
  `FenBrowser.Host\ProcessIsolation\*` non-production-grade files, especially `Gpu\GpuProcessIpc.cs`, `Utility\UtilityProcessIpc.cs`, `FrameSharedMemory.cs`, `Targets\*`, `RendererChildLoopIo.cs`, `RendererInputEvent.cs`, `IProcessIsolationCoordinator.cs`, `ProcessIsolationRuntime.cs`, `ProcessIsolationCoordinatorFactory.cs`, `Network\NetworkChildProcessHost.cs`
- Host shell hardening:
  `FenBrowser.Host\ChromeManager.cs`, `RootWidget.cs`, `Context\*`, `Input\*`, `Tabs\*`, `Theme\ThemeManager.cs`, non-production-grade `Widgets\*`, `FenBrowser.Host.csproj`, `app.manifest`

### FenBrowser.DevTools Coverage
- Core protocol/server/tooling:
  `FenBrowser.DevTools\Core\*` non-production-grade files
- Domain and DTO completion:
  `FenBrowser.DevTools\Domains\*` non-production-grade files, `Domains\DTOs\*`
- Instrumentation and panel completion:
  `FenBrowser.DevTools\Instrumentation\DomInstrumenter.cs`, `FenBrowser.DevTools\Panels\ElementsPanel.cs`

### FenBrowser.WebDriver Coverage
- Remove scaffold:
  `FenBrowser.WebDriver\Class1.cs`
- Protocol and command completion:
  `FenBrowser.WebDriver\CommandRouter.cs`, `Commands\*` non-production-grade files, `Protocol\*`, `Security\*`, `SessionManager.cs`, `FenBrowser.WebDriver.csproj`

### FenBrowser.WebIdlGen Coverage
- Deterministic tool hardening:
  `FenBrowser.WebIdlGen\Program.cs`, `FenBrowser.WebIdlGen.csproj`

## Definition Of Done For FenBrowser
An area is only considered production-grade when:
- It has one clear owner.
- It has no empty seams.
- Its security posture is explicit and logged.
- Its spec posture is explicit and logged.
- Its hot-path performance behavior is understood and measurable.
- Its failure handling is observable and recoverable.
- Its diagnostics are useful in production.
- Its temporary compatibility code is either removed or time-boxed with a migration owner.
- It no longer depends on "someone knows how this works" tribal knowledge.

## Strategic Direction
The audit says FenBrowser already has real engine substance.
This work ledger is about turning that substance into disciplined browser architecture.

The right move now is not to add random new features.
The right move is to close ownership, finish the unsafe seams, simplify the architecture, and force every important subsystem to meet production-grade standards before surface area expands again.
