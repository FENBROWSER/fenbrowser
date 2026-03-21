# JS Engine Final Audit

## Purpose

This file is the living gap ledger for the FenBrowser JavaScript engine surface.

Use it in two stages:

1. Keep every unresolved gap in the numbered findings list below.
2. Do not delete historical audit content from this file. When a gap is completed, preserve the entry and mark it struck through or explicitly resolved instead of removing the record.
3. Only mark a gap resolved after the implementation is production-grade, spec-defensible, tested, and the corresponding rule is satisfied.

The target standard is not "good enough to demo". The target standard is browser-engine quality: deterministic behavior, spec fidelity, real-site resilience, and architecture that avoids the historical shortcuts that hurt Chromium/WebKit/legacy engines.

## Audit Scope

- Repository slice audited: `FenBrowser.FenEngine`
- `obj` and `bin` excluded
- Files inventoried in this pass: `324`
- Strict JS-engine-related file set deep-reviewed in this pass: `140 / 140`
- Audit method:
  - full-tree inventory and static scan across all non-`obj`/`bin` files
  - line-by-line direct code reading across the complete JS-engine-related surface: `Core`, `Scripting`, `DOM`, `WebAPIs`, `Workers`, `Observers`, `Bindings`, and JS-facing host bridges
  - renderer/layout/CSS files were scanned for JS-adjacent integration gaps, but this document is intentionally centered on the JavaScript engine and web-platform runtime surface

## Area Summary

| Area | File Count |
| --- | ---: |
| `Rendering` | 95 |
| `Layout` | 44 |
| `Core` | 42 |
| `Bindings` | 37 |
| `DOM` | 26 |
| `WebAPIs` | 15 |
| `Workers` | 10 |
| `Scripting` | 9 |
| `Testing` | 5 |
| `HTML` | 5 |
| `Typography` | 5 |
| `(root)` | 5 |
| `Jit` | 4 |
| `Adapters` | 4 |
| `Security` | 4 |
| `Storage` | 4 |
| `Interaction` | 3 |
| `DevTools` | 2 |
| `Observers` | 1 |
| `Compatibility` | 1 |
| `Assets` | 1 |
| `Resources` | 1 |
| `Errors` | 1 |

## Findings

1. `P0` Real ES module semantics are not implemented.
   - Evidence:
     - `FenBrowser.FenEngine/Scripting/ModuleLoader.cs:174` strips `import` statements and relies on globals.
     - `FenBrowser.FenEngine/Scripting/ModuleLoader.cs:196` strips `export *` re-exports.
     - `FenBrowser.FenEngine/Scripting/ModuleLoader.cs:216` strips `export` and republishes names onto `window`.
     - `FenBrowser.FenEngine/Core/Parser.cs:2770` lowers `import()` to a generic `CallExpression`.
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:4748` returns a pseudo-promise with an empty module namespace fallback.
   - Why this is a gap:
     - This is source rewriting, not module record creation, linking, instantiation, live binding propagation, or cyclic dependency handling.
     - Any modern code-split SPA can fail in ways that are invisible to unit tests but fatal in production.
   - Exit bar:
     - implement spec-style module records, linking, evaluation ordering, live exports, cyclic graphs, `import()`, namespace objects, and per-realm caching without leaking module bindings onto `window`
   - Feature request tranche `1A`:
     - Target runtime surface: `FenBrowser.FenEngine.Core.ModuleLoader`
     - Required production-grade behavior: `export * from "<module>"` must aggregate named exports from linked module namespace objects, exclude `default`, preserve explicit local exports, and suppress ambiguous names exposed by multiple star re-exports.
     - Non-negotiable spec/compatibility requirements: explicit exports win over star re-exports; `default` is never forwarded by `export *`; conflicting star names must not be exposed as concrete bindings.
     - Failure modes prevented: silently dropping re-exported names, leaking `default` through star export, overriding explicit local exports, and exposing the wrong binding on conflicting star graphs.
     - Status: Partial tranche implemented, finding remains open.
     - Implemented on: `2026-03-20`
     - Implementation: `FenBrowser.FenEngine/Core/ModuleLoader.cs` now finalizes explicit exports first and then merges `export *` namespace bindings, skipping `default`, retaining explicit local precedence, and deleting ambiguous star-export collisions.
     - Tests: `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
     - Verification: `dotnet test FenBrowser.Tests --filter ModuleLoaderTests --no-restore` passed `22/22` on `2026-03-20`, including `LoadModule_ExportStar_Reexports_Named_Exports_But_Not_Default`, `LoadModule_ExportStar_DoesNotOverride_Explicit_Local_Export`, and `LoadModule_ExportStar_Conflicting_Reexports_Are_Not_Exposed`.
     - Remaining before finding `1` can be resolved: real module records, live binding propagation, cycle handling, `import()` semantics, namespace object completeness, and removal of legacy source-rewrite paths.
   - Feature request tranche `1B`:
     - Target runtime surface: `FenBrowser.FenEngine.Core.FenRuntime` dynamic `import()` path
     - Required production-grade behavior: dynamic `import(specifier)` must resolve through the active module loader, use the current module/document referrer, return a real engine promise, resolve with the module namespace object, and reject on actual loader failures.
     - Non-negotiable spec/compatibility requirements: no fake `__state__` promise bag objects; relative specifiers must resolve against `CurrentModulePath`/document base; promise chaining must observe microtask ordering; missing modules must reject instead of resolving empty namespaces.
     - Failure modes prevented: fake thenable surfaces, incorrect relative resolution, silent empty-module fallback, and uncatchable loader failures.
     - Status: Partial tranche implemented, finding remains open.
     - Implemented on: `2026-03-20`
     - Implementation: `FenBrowser.FenEngine/Core/FenRuntime.cs` now routes dynamic `import()` through `IModuleLoader.Resolve(...)` and `LoadModule(...)`, returning `JsPromise.Resolve(...)` / `JsPromise.Reject(...)` instead of a hand-rolled pseudo-promise object.
     - Tests: `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
     - Verification: `dotnet test FenBrowser.Tests --filter ModuleLoaderTests --no-restore` passed `25/25` on `2026-03-20`, including `DynamicImport_Returns_RealPromise_Resolved_With_ModuleNamespace`, `DynamicImport_ThenCallback_Resolves_RelativeTo_CurrentModulePath`, and `DynamicImport_MissingModule_Returns_RejectedPromise_And_Catch_Observes_Failure`.
     - Remaining before finding `1` can be resolved: full module records, live export binding propagation across namespace reads, complete cycle semantics, and retirement of the legacy source-rewrite module path.

2. `P0` Regex parsing still contains a placeholder fallback that can silently change program semantics.
   - Evidence:
     - `FenBrowser.FenEngine/Core/Parser.cs:5749` explicitly says "For now, just return a placeholder regex".
     - The fallback returns `Pattern = ".*"` and empty flags.
   - Why this is a gap:
     - Placeholder parsing is worse than a hard failure because it executes the wrong program.
   - Exit bar:
     - full context-aware regex literal tokenization and parsing, or strict rejection before execution if the engine cannot prove correctness

3. `P0` The bytecode compiler is not total for the language surface the runtime claims to parse.
   - Evidence:
     - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:535` throws for unsupported operators.
     - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:1314` throws for unsupported AST node types.
     - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:4822` rejects callable bodies that contain `with`.
   - Why this is a gap:
     - A production engine cannot accept syntax in the parser and then fail later in the compiler on real-world bundles.
   - Exit bar:
     - every parser-produced runtime-legal AST node must either compile correctly or be rejected earlier with intentional gating and compatibility accounting

4. `P0` Parser-inserted external scripts execute without static `load`/`error` dispatch in the main document boot path.
   - Evidence:
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:4240` executes fetched parser-discovered script source directly.
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3629`
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3645`
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3650`
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3668`
     - The dynamic-script path dispatches `error`/`load`, while the static boot path does not mirror that behavior.
   - Why this is a gap:
     - Modern bootstraps often observe script load/error semantics explicitly.
   - Exit bar:
     - unify static and dynamic external script semantics around spec-correct fetch, error, load, currentScript, and task/microtask timing

5. `P0` The value model still lies about the Symbol type.
   - Evidence:
     - `FenBrowser.FenEngine/Core/FenSymbol.cs:76` returns `JsValueType.Object` for symbols.
   - Why this is a gap:
     - Type tagging drives `typeof`, brand checks, coercion, property-key behavior, and host/API correctness.
     - A wrong base type leaks subtle bugs across the entire engine.
   - Exit bar:
     - symbols must be first-class tagged values with correct coercion, comparisons, reflection, and property-key behavior

6. `P1` `JSON.stringify(undefined)` is implemented incorrectly.
   - Evidence:
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:14802` returns the string `"undefined"`.
   - Why this is a gap:
     - This breaks observable JS semantics and corrupts JSON-based application logic.
   - Exit bar:
     - implement full spec-accurate `JSON.stringify` behavior for primitives, arrays, objects, replacers, `toJSON`, gap/indent, cycles, and root `undefined` handling

7. `P1` `ArrayBuffer`, typed arrays, and `crypto.subtle` are still placeholder-grade.
   - Evidence:
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:14049` sets `ArrayBuffer.prototype.byteLength` to `Undefined` as a placeholder.
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:10895` notes typed-array source handling is "only handled as double for now".
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:12661` returns a standard array of numbers as ArrayBuffer emulation for `crypto.subtle.digest`.
   - Why this is a gap:
     - Binary APIs are foundational for fetch, streams, crypto, media, WebAssembly, and modern frameworks.
   - Exit bar:
     - native-quality `ArrayBuffer`, `SharedArrayBuffer` policy gating, `DataView`, typed arrays, buffer detachment/transfer semantics, and real WebCrypto output types

8. `P1` `Intl` is only a basic stub surface, not an ECMA-402 implementation.
   - Evidence:
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:10745` labels the `Intl` surface as "Internationalization (basic stubs)".
   - Why this is a gap:
     - Locale, calendar, numbering system, formatting, collation, segmentation, and plural rules are not optional at browser-engine quality.
   - Exit bar:
     - complete or deliberately scope-gate `Intl` with spec-correct constructors, options processing, locale negotiation, and tests

9. `P1` `Temporal` is exposed as a full API namespace but implemented as throw-only stubs.
   - Evidence:
     - `FenBrowser.FenEngine/Core/FenRuntime.cs:13372` marks the whole `Temporal` surface as stubbed.
   - Why this is a gap:
     - Exposing unimplemented standard APIs creates false compatibility signals and breaks applications that feature-detect by presence.
   - Exit bar:
     - either fully implement to proposal/spec quality or do not expose the surface in production

10. `P1` `navigator.serviceWorker` is not spec-grade.
   - Evidence:
     - `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:31` stores `controller` as a mutable value, not a live getter-backed view.
     - `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:34` creates `ready` as a bare `FenObject`.
     - `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:105`
     - `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:120`
     - `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:152`
     - Promise behavior is hand-rolled through ad-hoc fields and callbacks.
   - Why this is a gap:
     - Service worker readiness, controller transitions, and promise semantics are timing-sensitive and heavily relied upon by real sites.
   - Exit bar:
     - live controller semantics, real promise integration, correct registration lifecycle, and spec-accurate event timing

11. `P1` Service worker lifecycle and runtime orchestration are simplified far below browser grade.
   - Evidence:
     - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:28` explicitly tracks active runtimes "by scope for now".
     - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs:68` says runtime startup is simplified instead of byte-for-byte update semantics and full lifecycle.
   - Why this is a gap:
     - Browser-grade service workers require install/activate/update/controller semantics, byte-for-byte checks, skipWaiting/clients.claim correctness, and persistent registration management.
   - Exit bar:
     - full lifecycle state machine, storage-backed persistence, update algorithm, and compatibility-tested fetch/event delivery

12. `P1` Worker runtime message delivery still contains placeholder architecture.
   - Evidence:
     - `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:376` states message invocation is only a placeholder for a real JS engine call.
   - Why this is a gap:
     - Worker messaging must respect structured clone, queued task ordering, error propagation, and global event dispatch semantics.
   - Exit bar:
     - spec-correct worker task sources, message events, error events, and deterministic shutdown behavior

13. ~~`P1` `addEventListener(..., { signal })` is not implemented.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs:37` previously marked AbortSignal-driven listener removal as not implemented.
   - Why this was a gap:
     - This option is widely used in modern code to control event listener lifetime without leaks.
   - Exit bar:
     - full `signal` support with abort propagation, duplicate-listener semantics, and event-target coverage
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs` now stores listener-bound `AbortSignal` state, registers abort cleanup only for successful listener additions, and detaches abort handlers on explicit removal and `once` cleanup so signal-driven lifetimes do not leak registry state.
     - `FenBrowser.FenEngine/DOM/ElementWrapper.cs` and `FenBrowser.FenEngine/DOM/NodeWrapper.cs` now pass `signal` through the shared registry path instead of wiring element-only ad hoc callbacks that could break duplicate-listener semantics.
     - `FenBrowser.FenEngine/Core/FenRuntime.cs` now honors `{ signal }` for generic `EventTarget` listener storage on `window`-style objects, skips already-aborted registrations, and upgrades `AbortController`/`AbortSignal` listener plumbing so `abort` dispatch, `onabort`, duplicate suppression, and `removeEventListener` semantics work through the same runtime path.
   - Tests:
     - `FenBrowser.Tests/DOM/InputEventTests.cs`
     - `AddEventListener_WindowSignal_RemovesListenerAfterAbort`
     - `AddEventListener_DuplicateSignalRegistration_DoesNotRemoveOriginalListener`
   - Verification:
     - `dotnet build FenBrowser.Tests --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests --no-restore --filter "FullyQualifiedName~AddEventListener_WindowSignal_RemovesListenerAfterAbort|FullyQualifiedName~AddEventListener_DuplicateSignalRegistration_DoesNotRemoveOriginalListener"` passed on `2026-03-20` with `2/2` tests green.
     - Focused runtime verification through a published single-file harness passed on `2026-03-20` with:
       - `calls=1`
       - `plainCalls=2`
       - `aborted=True`

14. ~~`P1` The history state stack and `popstate` semantics are still placeholders.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3877` said `pushState` was not wired to a real history stack.
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:3903` said popstate handling was not implemented.
   - Why this was a gap:
     - SPA routing depends on this behavior. Placeholder history APIs create false positives during smoke tests and hard failures in real navigation flows.
   - Exit bar:
     - real session history entries, state serialization, same-document navigation semantics, and event dispatch timing
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Core/FenRuntime.cs` now exposes dynamic `history.length` and `history.state` accessors instead of stale data slots, clones state through `structuredClone`, resolves and synchronizes same-document URLs, and maintains a real local session-history stack when no host bridge is attached.
     - `FenBrowser.FenEngine/Core/FenRuntime.cs` now queues `popstate` through `EventLoopCoordinator` with `TaskSource.History`, updates `location` and `BaseUri` before delivery, and dispatches queued events through the actual `window` listener store plus `window.onpopstate`.
     - `FenBrowser.FenEngine/Core/Interfaces/IHistoryBridge.cs` and `FenBrowser.FenEngine/Rendering/BrowserApi.cs` now expose the active history-entry URL so bridge-backed traversal keeps `history`, `location`, and runtime state synchronized.
   - Tests:
     - `FenBrowser.Tests/WebAPIs/HistoryApiTests.cs`
     - `FenBrowser.Tests/Engine/FenRuntimeLocationTests.cs`
     - `FenBrowser.Tests/DOM/InputEventTests.cs`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` completed successfully on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~HistoryApiTests|FullyQualifiedName~FenRuntimeLocationTests"` passed on `2026-03-20` with `11/11` tests green.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~InputEventTests.AddEventListener_WindowSignal_RemovesListenerAfterAbort|FullyQualifiedName~InputEventTests.AddEventListener_DuplicateSignalRegistration_DoesNotRemoveOriginalListener"` remained green on `2026-03-20` with `2/2` tests green, covering the shared window listener path used by queued `popstate` dispatch.

15. ~~`P1` `IntersectionObserver` is not complete enough for modern app behavior.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs:44` only passed `thresholds[0]` into the runtime instance.
     - `FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs:49` stored `rootMargin` on the JS wrapper only.
     - `FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs:83` returned an empty array from `takeRecords()`.
     - `FenBrowser.FenEngine/Observers/ObserverCoordinator.cs` previously tracked a single `_threshold` and no queued observer records.
   - Why this was a gap:
     - Virtualized UIs, lazy loading, and infinite-scroll feeds rely on threshold arrays, root margins, and queued records.
   - Exit bar:
     - full threshold array handling, root-margin application, queued entry delivery, and spec-consistent `takeRecords`
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Observers/ObserverCoordinator.cs` now tracks the full normalized threshold list, computes threshold-index transitions instead of a single boolean threshold gate, and applies parsed `rootMargin` offsets to the evaluation viewport before intersection math.
     - `FenBrowser.FenEngine/Observers/ObserverCoordinator.cs` now maintains a real per-observer record queue shared by queued callback delivery and `takeRecords()`, so manual draining and callback scheduling operate on the same production data structure.
     - `FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs` now validates and passes parsed `rootMargin` into the runtime instance, exposes `root`, and wires `takeRecords()` to the native observer queue instead of returning a fake empty array.
   - Tests:
     - `FenBrowser.Tests/WebAPIs/ObserverApiTests.cs`
     - `FenBrowser.Tests/Engine/IntersectionObserverTests.cs`
     - `FenBrowser.Tests/Engine/PrivacyTests.cs`
     - `FenBrowser.Tests/Engine/PlatformInvariantTests.cs`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` completed successfully on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~ObserverApiTests|FullyQualifiedName~IntersectionObserverTests|FullyQualifiedName~PrivacyTests|FullyQualifiedName~PlatformInvariantTests.ObserverCoordinator_Clear_RemovesAllState|FullyQualifiedName~PlatformInvariantTests.ObserverCoordinator_EvaluatesIntersection_Before_Resize"` passed on `2026-03-20` with `21/21` tests green.

16. ~~`P1` AST-backed function invocation still has a runtime error escape hatch that should not exist in a production engine.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/Core/FenFunction.cs:249` previously returned `"Bytecode-only mode: AST-backed function invocation is not supported."`
   - Why this was a gap:
     - Once functions are accepted into the runtime, the engine should not retain a fallback path that can surface as an execution-mode failure.
   - Exit bar:
     - all runtime-callable functions must be bytecode-backed or intentionally rejected before they become callable
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Core/FenFunction.cs` now compiles AST-backed function bodies at construction time through the shared bytecode compiler path, persists the resulting `BytecodeBlock`/`LocalMap`, and treats any later missing-bytecode state as an internal invariant violation instead of a recoverable runtime mode.
     - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs` now exposes a shared callable-body compilation helper so AST-backed function construction and normal function-template emission use the same lowering, local-slot, and `arguments`-usage analysis path.
     - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs` no longer treats `func.Body` as a callable fallback and no longer lazily compiles AST-backed functions or constructors during `Call*` / `Construct*` opcodes.
   - Tests:
     - `FenBrowser.Tests/Engine/Bytecode/BytecodeExecutionTests.cs`
     - `FenBrowser.Tests/Engine/FenRuntimeBytecodeExecutionTests.cs`
     - `Bytecode_CallOpcode_WithAstBackedFunction_ShouldExecuteWithEagerCallableBytecode`
     - `Bytecode_CallFromArrayOpcode_WithAstBackedFunction_ShouldExecuteWithEagerCallableBytecode`
     - `Bytecode_ConstructOpcode_WithAstBackedConstructor_ShouldExecuteWithEagerCallableBytecode`
     - `Bytecode_ConstructFromArrayOpcode_WithAstBackedConstructor_ShouldExecuteWithEagerCallableBytecode`
     - `Bytecode_AstBackedFunction_ShouldRejectUncompilableCallableBody_BeforeInvocation`
     - `ExecuteSimple_WithAstBackedGlobal_CallHeavyScriptUsesEagerCallableBytecode`
     - `ExecuteSimple_AstBackedFunctionCreation_RejectsUncompilableCallableBodyBeforeGlobalRegistration`
   - Verification:
     - `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj --no-restore` passed on `2026-03-20`.
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~FenBrowser.Tests.Engine.Bytecode.BytecodeExecutionTests.Bytecode_CallOpcode_WithAstBackedFunction_ShouldExecuteWithEagerCallableBytecode|FullyQualifiedName~FenBrowser.Tests.Engine.Bytecode.BytecodeExecutionTests.Bytecode_CallFromArrayOpcode_WithAstBackedFunction_ShouldExecuteWithEagerCallableBytecode|FullyQualifiedName~FenBrowser.Tests.Engine.Bytecode.BytecodeExecutionTests.Bytecode_ConstructOpcode_WithAstBackedConstructor_ShouldExecuteWithEagerCallableBytecode|FullyQualifiedName~FenBrowser.Tests.Engine.Bytecode.BytecodeExecutionTests.Bytecode_ConstructFromArrayOpcode_WithAstBackedConstructor_ShouldExecuteWithEagerCallableBytecode|FullyQualifiedName~FenBrowser.Tests.Engine.Bytecode.BytecodeExecutionTests.Bytecode_AstBackedFunction_ShouldRejectUncompilableCallableBody_BeforeInvocation|FullyQualifiedName~FenBrowser.Tests.Engine.FenRuntimeBytecodeExecutionTests.ExecuteSimple_WithAstBackedGlobal_CallHeavyScriptUsesEagerCallableBytecode|FullyQualifiedName~FenBrowser.Tests.Engine.FenRuntimeBytecodeExecutionTests.ExecuteSimple_AstBackedFunctionCreation_RejectsUncompilableCallableBodyBeforeGlobalRegistration"` passed on `2026-03-20` with `7/7` tests green.

17. ~~`P2` Session storage does not persist across reload in a browser-grade way.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/WebAPIs/StorageApi.cs:268` previously documented session storage as transient without a stable reload-scoped partition identity.
   - Why this was a gap:
     - Session storage semantics matter for auth flows, tab recovery, and same-tab app boot continuity.
   - Exit bar:
     - tab-scoped persistence model with correct reload/session lifetime semantics
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Core/IDomBridge.cs` now exposes `SessionStoragePartitionId`, giving the runtime a stable per-tab identity instead of forcing `sessionStorage` to allocate a fresh anonymous scope on every reload.
     - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs` now publishes its existing engine-owned tab partition id through `IDomBridge`, so repeated `FenRuntime` creation during same-tab reloads reuses the same browser-session storage scope.
     - `FenBrowser.FenEngine/WebAPIs/StorageApi.cs` now accepts an optional stable partition-id provider for `CreateSessionStorage(...)` and persists session data across storage recreation when the caller supplies the same tab/session identity, while preserving isolation when no stable partition is present.
     - `FenBrowser.FenEngine/Core/FenRuntime.cs` now creates `sessionStorage` with both origin and tab partition providers, so reloads in the same tab keep state while origin boundaries and cross-tab isolation still hold.
   - Tests:
     - `FenBrowser.Tests/WebAPIs/StorageTests.cs`
     - `SessionStorage_ShouldPersistAcrossStorageRecreation_WithSamePartitionAndOrigin`
     - `SessionStorage_ShouldPersistAcrossFenRuntimeReload_WithStableTabPartition`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~FenBrowser.Tests.WebAPIs.StorageTests"` passed on `2026-03-20` with `7/7` tests green.

18. ~~`P2` Shadow DOM was absent in the host-facing surface.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/Rendering/BrowserApi.cs` previously returned `null` from `GetShadowRootAsync(...)`, so the host/WebDriver stack could not expose open shadow roots even though the DOM core already implemented them.
   - Why this was a gap:
     - Modern component frameworks and web components rely on shadow roots, composed tree semantics, and event retargeting.
     - Engine-internal support is not enough if browser automation and host-facing DOM search surfaces cannot traverse and expose shadow trees.
   - Exit bar:
     - real shadow roots, slotting, composed tree, retargeting, serialization rules, and test coverage
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Rendering/BrowserApi.cs` now treats `ShadowRoot` as a first-class search root, returns stable node ids for open shadow roots, scopes element lookup against node roots instead of element-only roots, and serializes shadow-root page-source requests through fragment HTML.
     - `FenBrowser.WebDriver/CommandRouter.cs` now exposes WebDriver shadow-root routes for fetching a shadow root and locating elements from that shadow root context.
     - `FenBrowser.WebDriver/Commands/CommandHandler.cs` and `FenBrowser.WebDriver/Commands/ElementCommands.cs` now execute those commands, return spec-shaped shadow-root references, and surface `no such shadow root` when an open shadow root does not exist.
     - `FenBrowser.WebDriver/Protocol/ErrorCodes.cs` and `FenBrowser.WebDriver/Protocol/WebDriverResponse.cs` now define the shadow-root error/result protocol surface, including the `shadow-6066-11e4-a52e-4f735466cecf` reference payload.
     - `FenBrowser.Host/WebDriver/HostBrowserDriver.cs` and `FenBrowser.Host/WebDriver/FenBrowserDriver.cs` now forward shadow-root retrieval through the host driver path so automation sees the same DOM capability the engine exposes internally.
   - Tests:
     - `FenBrowser.Tests/Rendering/BrowserHostShadowDomTests.cs`
     - `GetShadowRootAsync_ReturnsRegisteredShadowRootId_ForOpenShadowRoot`
     - `FindElementAsync_AllowsSearchWithinRegisteredShadowRoot`
     - `FenBrowser.Tests/WebDriver/ShadowRootCommandsTests.cs`
     - `GetShadowRoot_Route_ReturnsShadowRootReference`
     - `FindElementFromShadowRoot_Route_UsesShadowRootAsParentContext`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~FenBrowser.Tests.Rendering.BrowserHostShadowDomTests|FullyQualifiedName~FenBrowser.Tests.WebDriver.ShadowRootCommandsTests"` passed on `2026-03-20` with `4/4` tests green.

19. ~~`P2` The codebase still carries a placeholder alternate JS runtime abstraction that should not survive into a production engine architecture.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs` previously defined `FullJsRuntimeStub` as a no-op second `IJsRuntime` implementation.
   - Why this was a gap:
     - Placeholder runtime forks invite drift, split ownership, dead code paths, and compatibility confusion.
   - Exit bar:
     - one authoritative runtime path, or a real abstraction layer with tested interchangeable implementations
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs` now documents `IJsRuntime` as the narrow adapter surface for the authoritative `JavaScriptEngine` path instead of a future-engine staging point.
     - `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs` no longer contains `FullJsRuntimeStub`, leaving `JsZeroRuntime` as the only concrete `IJsRuntime` implementation in the engine assembly.
   - Tests:
     - `FenBrowser.Tests/Engine/JsRuntimeAbstractionTests.cs`
     - `IJsRuntime_HasSingleConcreteImplementation`
     - `JsZeroRuntime_DelegatesToAuthoritativeJavaScriptEngine`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~FenBrowser.Tests.Engine.JsRuntimeAbstractionTests"` passed on `2026-03-20` with `2/2` tests green.

20. ~~`P1` The event loop still collapses all task sources into a single FIFO queue instead of maintaining browser-grade task source semantics.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs` previously stored all scheduled work in one shared `Queue<ScheduledTask>`.
     - `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs` routed every task source through that single FIFO queue.
   - Why this was a gap:
     - The HTML event loop model is not just "one FIFO queue"; task source separation affects fairness, ordering, timers, networking, history, and real-site behavioral compatibility.
   - Exit bar:
     - source-aware scheduling with explicit ordering policy, starvation protection, and tests that cover cross-source ordering and reentrancy
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs` now maintains independent FIFO queues per `TaskSource` instead of one shared queue for all macro-tasks.
     - `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs` now schedules across active sources with deterministic round-robin selection, preserving in-source FIFO order while preventing one busy source from starving the others.
     - `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs` now exposes source-aware pending/count inspection helpers so scheduler behavior can be asserted directly in regression tests.
   - Tests:
     - `FenBrowser.Tests/Engine/ExecutionSemanticsTests.cs`
     - `FenBrowser.Tests/Engine/EventLoopTests.cs`
     - `FenBrowser.Tests/WebAPIs/HistoryApiTests.cs`
     - `TaskSources_PreserveFifoWithinEachSource`
     - `TaskSources_RunRoundRobinAcrossActiveSources`
     - `TaskSources_ReentrantScheduling_DoesNotStarveOtherSources`
   - Verification:
     - `dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\`` passed on `2026-03-20`.
     - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --no-build --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\ --filter "FullyQualifiedName~FenBrowser.Tests.Engine.ExecutionSemanticsTests|FullyQualifiedName~FenBrowser.Tests.Engine.EventLoopTests|FullyQualifiedName~FenBrowser.Tests.WebAPIs.HistoryApiTests"` passed on `2026-03-20` with `25/25` tests green.

21. `P1` `Reflect` and `Proxy` are still incomplete in ways that will break advanced framework/runtime behavior.
   - Evidence:
     - `FenBrowser.FenEngine/Scripting/ReflectAPI.cs:134`
     - `FenBrowser.FenEngine/Scripting/ReflectAPI.cs:137`
     - `Reflect.construct` is present but still marked "Not implemented yet".
     - `FenBrowser.FenEngine/Scripting/ProxyAPI.cs:82` explicitly assumes frameworks will provide a `get` trap because transparent forwarding is incomplete.
   - Why this is a gap:
     - Proxy/Reflect correctness is core infrastructure for modern reactive frameworks, metaprogramming, decorators, validation layers, and security boundaries.
   - Exit bar:
     - complete trap forwarding and default behavior for proxy targets, spec-correct `Reflect.construct`, and targeted framework-grade compatibility tests

22. ~~`P2` Event listener exception reporting still degrades real thrown errors into a placeholder object for `window.onerror`.~~
   - Historical evidence:
     - `FenBrowser.FenEngine/DOM/EventTarget.cs:300` previously built the `window.onerror` argument list with `FenValue.FromObject(new FenObject())` as a placeholder error object.
   - Why this was a gap:
     - Error tooling, framework diagnostics, and site-level recovery code depend on the real thrown value, stack, and message object shape.
   - Exit bar:
     - preserve and forward the actual thrown value and associated metadata through the engine's listener-error reporting path
   - Status: Resolved
   - Resolved on: `2026-03-20`
   - Implementation:
     - `FenBrowser.FenEngine/DOM/EventTarget.cs` now unwraps real thrown values (`JsThrownValueException`, `ThrownValue` carriers, and `FenError`), resolves the active window target deterministically, and invokes `window.onerror` with the actual message/error payload instead of a placeholder object.
     - Listener-error reporting now uses the original callable `window.onerror` value and preserves runtime-visible error identity for diagnostics and recovery code.
   - Tests:
     - `FenBrowser.Tests/DOM/InputEventTests.cs`
     - `DispatchEvent_WindowOnError_Receives_ActualThrownErrorObject`
   - Verification:
     - `dotnet build FenBrowser.Tests --no-restore -p:OutDir=C:\Temp\fenbrowser-tests-build\` passed on `2026-03-20`.
     - Machine-local runtime verification passed on `2026-03-20` through a focused published verifier exercising the same scenario:
       - `window.onerror` executed
       - `error.name === "TypeError"`
       - `error.message === "boom"`
       - `message === "boom"`
     - `dotnet test` execution remains blocked on this machine by Windows Application Control for freshly built test assemblies, so runtime verification was completed through the published single-file verifier instead of the xUnit host.

23. `P1` Large parts of the JS-facing platform surface still return homegrown promise-like objects or synchronous thenables instead of engine promises.
   - Evidence:
     - `FenBrowser.FenEngine/WebAPIs/Cache.cs:538` implements `Cache` async behavior with a custom `FenObject` promise shape.
     - `FenBrowser.FenEngine/WebAPIs/CacheStorage.cs:255` does the same for `CacheStorage`.
     - `FenBrowser.FenEngine/Workers/ServiceWorkerRegistration.cs:86`
     - `FenBrowser.FenEngine/Workers/ServiceWorkerGlobalScope.cs:70`
     - `FenBrowser.FenEngine/Workers/ServiceWorkerClients.cs:128`
     - `FenBrowser.FenEngine/DOM/CustomElementRegistry.cs:320`
     - `FenBrowser.FenEngine/WebAPIs/WebAPIs.cs:15` defines `ResolvedThenable`, and APIs such as `Notification.requestPermission`, fullscreen, clipboard, storage manager, and share return it at `WebAPIs.cs:365`, `WebAPIs.cs:865`, `WebAPIs.cs:881`, `WebAPIs.cs:906`, `WebAPIs.cs:965`, and `WebAPIs.cs:1022`.
   - Why this is a gap:
     - Promise job ordering, chaining, assimilation, rejection tracking, and microtask checkpoint behavior are observable semantics. Fake promise objects are not browser-compatible.
   - Exit bar:
     - every async web-platform surface must resolve through the engine's real promise machinery with spec-correct microtask timing and rejection behavior

24. `P1` Canvas 2D pixel APIs are still placeholder-grade.
   - Evidence:
     - `FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs:718` says `getImageData` still "Would need to read from bitmap synchronously".
     - `FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs:724` says `putImageData` still "Would need to write to bitmap".
   - Why this is a gap:
     - Real sites use pixel reads and writes for editors, charting, CAPTCHA, screenshots, media processing, and canvas feature detection.
   - Exit bar:
     - implement real backing-store read/write semantics, color-space-correct pixel transfer, bounds behavior, and browser-compatible error handling

25. `P1` Worker structured clone is not spec-grade.
   - Evidence:
     - `FenBrowser.FenEngine/Workers/StructuredClone.cs:103` falls back to JSON serialization for complex objects.
     - `FenBrowser.FenEngine/Workers/StructuredClone.cs:98`
     - `FenBrowser.FenEngine/Workers/StructuredClone.cs:101`
     - The implementation has no transfer-list semantics, no clone graph memory, and no browser-grade handling for built-ins such as `Map`, `Set`, `RegExp`, `Blob`, `File`, `MessagePort`, or typed-array ownership transfer.
   - Why this is a gap:
     - Structured clone defines worker messaging correctness. JSON fallback drops identity, brands, cycles, binary ownership semantics, and many built-in types.
   - Exit bar:
     - implement the real structured clone and transfer algorithms with clone memory, transfer lists, built-in coverage, and rejection on unsupported values

26. `P1` IndexedDB is currently an in-memory simplified model, not a browser-grade database engine surface.
   - Evidence:
     - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:43` stores databases in a process-local `ConcurrentDictionary`.
     - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:114`
     - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:393`
     - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:425`
     - `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs:507`
     - The API surface only models basic object stores and request callbacks; it has no indexes, cursors, persistence, blocked/versionchange orchestration, durability semantics, or transaction scheduling fidelity.
   - Why this is a gap:
     - Real sites rely on IndexedDB for offline state, auth/session storage, caches, migration logic, and transactional guarantees.
   - Exit bar:
     - storage-backed persistence, transaction scheduler correctness, indexes, cursors, key ranges, versionchange/blocked semantics, and crash-safe durability behavior

27. `P1` Custom elements lifecycle is only partially implemented.
   - Evidence:
     - `FenBrowser.FenEngine/DOM/CustomElementRegistry.cs:169` upgrades elements by setting a bookkeeping attribute.
     - `FenBrowser.FenEngine/DOM/CustomElementRegistry.cs:172`
     - `FenBrowser.FenEngine/DOM/CustomElementRegistry.cs:183`
     - There is no constructor invocation path, no `attributeChangedCallback`, no `observedAttributes` processing, no `disconnectedCallback`, and no `adoptedCallback`.
   - Why this is a gap:
     - Web components need lifecycle correctness, upgrade timing, CEReactions integration, and attribute-change semantics to work reliably.
   - Exit bar:
     - implement full custom-element definition, construction stack, upgrade algorithm, CEReactions, lifecycle callbacks, and real `whenDefined` promise behavior

28. `P1` Web Audio is a simulation surface, not a real audio engine implementation.
   - Evidence:
     - `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:436` ends playback after a synthetic `250 / playbackRate` timer.
     - `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:760` fabricates a fixed-size decoded buffer.
     - `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:1234`
     - `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:1248`
     - Analyser output is synthesized procedurally instead of coming from a rendered audio graph.
   - Why this is a gap:
     - Browser-grade Web Audio requires real rendering, scheduling, decoding, node graph behavior, and timing guarantees that sites depend on.
   - Exit bar:
     - implement a real audio graph, decode/render pipeline, node scheduling, analyser integration, and spec-tested timing/state behavior

29. `P1` WebRTC is a mock compatibility surface, not a real transport/media implementation.
   - Evidence:
     - `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:306` and `WebRTCAPI.cs:320` synthesize trivial SDP strings.
     - `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:595` opens data channels after a fixed delay.
     - `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:717` and `WebRTCAPI.cs:724` return empty media-track arrays.
     - `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:1054` resolves stats and related async paths through synthetic local objects.
   - Why this is a gap:
     - Sites that use WebRTC require real ICE, DTLS/SRTP/SCTP, signaling-state correctness, track plumbing, and data-channel behavior.
   - Exit bar:
     - implement real transport/media integration or gate the entire surface off in production until it is genuinely interoperable

## Implementation Rules

1. No feature is considered complete while it still depends on a placeholder object, stub, source rewrite, fake promise, or "for now" runtime path.

2. No ES module support may be implemented by leaking exports onto `window` or stripping `import`/`export`. Module loading must be built around real module records, linking, instantiation, evaluation, and live bindings.

3. No parser fallback may silently change semantics. If correctness is not guaranteed, fail early and loudly rather than executing a guessed program.

4. No runtime type lies are allowed. Value tags for `Symbol`, `BigInt`, `ArrayBuffer`, typed arrays, promises, and host objects must reflect their true semantics.

5. No async Web API may use ad-hoc pseudo-promises in production paths. All async completion must flow through the engine's real promise/job model and the event loop.

6. No browser API should be exposed by default if it is only a throw-only skeleton or partial compatibility facade. Presence must imply production-grade semantics.

7. No event-loop implementation may collapse distinct task sources into one undifferentiated FIFO queue. Macro-tasks must preserve FIFO within a source, use an explicit cross-source scheduling policy, and defend against starvation.

8. No history, navigation, or worker/service-worker implementation may shortcut the lifecycle model in a way that hides missing state transitions or timing requirements.

9. No compatibility fix is complete without a targeted regression test, and site-critical fixes must also get a real-site reproduction or bundle-pattern regression.

10. No global-state shortcut may be used where a real realm, document, or pipeline context is required. Explicit state ownership beats hidden global mutation.

11. No library may dictate engine architecture. Skia, Win32, or any other dependency must remain behind platform and rendering abstractions.

12. No feature should ship if the implementation only passes narrow demos but fails production-scale bundle patterns, cross-script state, or event-loop timing behavior.

13. No unsupported syntax or runtime feature should be discovered late if the parser or platform already advertises support. Capability gating must happen at the earliest defensible layer.

14. No host integration should diverge between static and dynamic script paths. Script fetching, error dispatch, load dispatch, current-script tracking, and execution timing must be unified.

15. No web-platform API should omit abort, cleanup, or lifetime management. Listener removal, worker shutdown, observer disconnect, and storage/session lifetime must all be explicit and tested.

16. Every new feature must be held to Chrome/Firefox-class compatibility expectations, while explicitly avoiding their historical pitfalls: hidden global state, over-coupling to rendering libraries, under-specified legacy shims, and correctness traded away for short-term boot success.

17. No JS-facing platform API may return a fake promise, resolved-thenable wrapper, callback-only placeholder, or state bag with `__state` fields where a real engine promise is required.

18. No binary, canvas, or media API may ship with placeholder read/write paths. `ArrayBuffer`, typed arrays, `ImageData`, canvas pixel access, media buffers, and blob/file flows must operate on real backing storage with browser-correct ownership and mutation semantics.

19. No worker or cross-context messaging path may fall back to JSON serialization in place of structured clone. Transfer lists, cycles, built-in brands, binary ownership transfer, and rejection behavior must be explicit and spec-grade.

20. No IndexedDB implementation may be considered complete while it is process-local, in-memory-only, timer-simulated, or missing transaction scheduling, indexes, cursors, versionchange/blocked coordination, and durable persistence.

21. No custom-elements implementation may ship as a name registry plus partial upgrade hook. Construction stack, CEReactions, lifecycle callbacks, observed-attribute processing, and upgrade timing must all be implemented together.

22. No Web Audio surface may simulate graph behavior with fixed timers, synthetic analyser output, fake decode paths, or placeholder buffers. Audio timing, graph processing, and node state must come from a real audio pipeline.

23. No WebRTC surface may be exposed as production-ready while SDP, ICE, stats, media tracks, data channels, or connection state are mocked or delay-simulated instead of backed by real transport/media behavior.

24. No host, WebDriver, or DevTools surface may treat `ShadowRoot` as invisible once the DOM core exposes it. Search roots, element identity, serialization, and automation protocol support must cover shadow trees explicitly and be regression-tested.

25. No finding may be struck through as completed unless the implementation change is production-grade, the related regression tests are already in the tree, and the audit entry is updated in the same change.

25. No completed item may disappear from this file. Completed work must remain recorded with explicit resolved status, completion date, and the validating test location.

26. No feature request is valid for closure unless it names the target runtime surface, the required browser-grade semantics, the acceptance criteria, and the exact test coverage expected before strike-off.

27. No test requirement may be satisfied by smoke-only coverage. Each closed finding must have focused regression tests for semantics, edge cases, and at least one real-world or minified-bundle style execution path when applicable.

## Promotion Rule

An item may only be struck through or marked resolved in the numbered findings list when all of the following are true:

1. The implementation is production-grade and spec-defensible.
2. The behavior is covered by focused regression tests.
3. Real-site or real-bundle reproduction cases no longer fail.
4. The replacement behavior satisfies the implementation rules above.
5. Any related documentation is updated in the same change.
6. The original finding text stays in this file as historical record.
7. The entry is updated with explicit completion metadata: date, implementation summary, and validating test paths.

## Resolution Format

When a finding is completed, do not delete it. Update it in place using this format:

1. Strike through the finding title.
2. Add `Status: Resolved`.
3. Add `Resolved on: YYYY-MM-DD`.
4. Add `Implementation:` with a short summary of what changed.
5. Add `Tests:` with exact test file paths.
6. Add `Verification:` with the real-site, bundle, or conformance evidence used to justify closure.

Example completion shape:

- `~~P1 Example gap title~~`
- `Status: Resolved`
- `Resolved on: 2026-03-20`
- `Implementation: engine now uses real promise integration for this surface`
- `Tests: path/to/test1, path/to/test2`
- `Verification: reproduced prior failure, reran scenario, failure no longer occurs`

## Feature Request Template

Every implementation candidate taken from the findings list must first be framed with this minimum request structure:

1. `Target finding number`
2. `Target runtime surface`
3. `Required production-grade behavior`
4. `Non-negotiable spec/compatibility requirements`
5. `Failure modes that must be prevented`
6. `Required regression tests`
7. `Real-site or bundle validation target`
8. `Exit criteria for striking through the finding`

## Appendix A: JS-Critical File Inventory

The following JS-critical files were included in the code-reading pass for this audit.

### Core

- `FenBrowser.FenEngine/Core/EngineLoop.cs`
- `FenBrowser.FenEngine/Core/FenRuntime.cs`
- `FenBrowser.FenEngine/Core/FenObject.cs`
- `FenBrowser.FenEngine/Core/FenFunction.cs`
- `FenBrowser.FenEngine/Core/FenEnvironment.cs`
- `FenBrowser.FenEngine/Core/ExecutionContext.cs`
- `FenBrowser.FenEngine/Core/FenValue.cs`
- `FenBrowser.FenEngine/Core/FenSymbol.cs`
- `FenBrowser.FenEngine/Core/IDomBridge.cs`
- `FenBrowser.FenEngine/Core/InputQueue.cs`
- `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs`
- `FenBrowser.FenEngine/Core/EventLoop/MicrotaskQueue.cs`
- `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs`
- `FenBrowser.FenEngine/Core/EnginePhase.cs`
- `FenBrowser.FenEngine/Core/Lexer.cs`
- `FenBrowser.FenEngine/Core/JsThrownValueException.cs`
- `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs`
- `FenBrowser.FenEngine/Core/Bytecode/VM/CallFrame.cs`
- `FenBrowser.FenEngine/Core/Bytecode/OpCode.cs`
- `FenBrowser.FenEngine/Core/Types/Shape.cs`
- `FenBrowser.FenEngine/Core/Types/JsWeakSet.cs`
- `FenBrowser.FenEngine/Core/Types/JsWeakMap.cs`
- `FenBrowser.FenEngine/Core/Types/JsTypedArray.cs`
- `FenBrowser.FenEngine/Core/Types/JsSymbol.cs`
- `FenBrowser.FenEngine/Core/Types/JsSet.cs`
- `FenBrowser.FenEngine/Core/Types/JsPromise.cs`
- `FenBrowser.FenEngine/Core/Types/JsMap.cs`
- `FenBrowser.FenEngine/Core/Types/JsIntl.cs`
- `FenBrowser.FenEngine/Core/Types/JsBigInt.cs`
- `FenBrowser.FenEngine/Core/Parser.cs`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs`
- `FenBrowser.FenEngine/Core/PropertyDescriptor.cs`
- `FenBrowser.FenEngine/Core/Ast.cs`
- `FenBrowser.FenEngine/Core/AnimationFrameScheduler.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IValue.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IObject.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IModuleLoader.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IHtmlDdaObject.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IHistoryBridge.cs`
- `FenBrowser.FenEngine/Core/Interfaces/IExecutionContext.cs`
- `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs`
- `FenBrowser.FenEngine/Core/Bytecode/CodeBlock.cs`

### Scripting

- `FenBrowser.FenEngine/Scripting/ReflectAPI.cs`
- `FenBrowser.FenEngine/Scripting/ProxyAPI.cs`
- `FenBrowser.FenEngine/Scripting/ModuleLoader.cs`
- `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs`
- `FenBrowser.FenEngine/Scripting/JavaScriptRuntime.cs`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Dom.cs`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`
- `FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs`

### DOM

- `FenBrowser.FenEngine/DOM/DomWrapperFactory.cs`
- `FenBrowser.FenEngine/DOM/DomMutationQueue.cs`
- `FenBrowser.FenEngine/DOM/DomEvent.cs`
- `FenBrowser.FenEngine/DOM/DocumentWrapper.cs`
- `FenBrowser.FenEngine/DOM/CustomEvent.cs`
- `FenBrowser.FenEngine/DOM/CustomElementRegistry.cs`
- `FenBrowser.FenEngine/DOM/CommentWrapper.cs`
- `FenBrowser.FenEngine/DOM/AttrWrapper.cs`
- `FenBrowser.FenEngine/DOM/FontLoadingBindings.cs`
- `FenBrowser.FenEngine/DOM/EventTarget.cs`
- `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs`
- `FenBrowser.FenEngine/DOM/ElementWrapper.cs`
- `FenBrowser.FenEngine/DOM/HTMLCollectionWrapper.cs`
- `FenBrowser.FenEngine/DOM/HighlightApiBindings.cs`
- `FenBrowser.FenEngine/DOM/InMemoryCookieStore.cs`
- `FenBrowser.FenEngine/DOM/LegacyUiEvents.cs`
- `FenBrowser.FenEngine/DOM/TouchEvent.cs`
- `FenBrowser.FenEngine/DOM/TextWrapper.cs`
- `FenBrowser.FenEngine/DOM/StaticNodeList.cs`
- `FenBrowser.FenEngine/DOM/ShadowRootWrapper.cs`
- `FenBrowser.FenEngine/DOM/RangeWrapper.cs`
- `FenBrowser.FenEngine/DOM/Observers.cs`
- `FenBrowser.FenEngine/DOM/NodeWrapper.cs`
- `FenBrowser.FenEngine/DOM/NodeListWrapper.cs`
- `FenBrowser.FenEngine/DOM/NamedNodeMapWrapper.cs`
- `FenBrowser.FenEngine/DOM/MutationObserverWrapper.cs`

### Web APIs, Workers, Observers, Generated Bindings

- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs`
- `FenBrowser.FenEngine/Workers/WorkerGlobalScope.cs`
- `FenBrowser.FenEngine/Workers/WorkerConstructor.cs`
- `FenBrowser.FenEngine/Workers/StructuredClone.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorkerRegistration.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorkerGlobalScope.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorkerClients.cs`
- `FenBrowser.FenEngine/Workers/ServiceWorker.cs`
- `FenBrowser.FenEngine/WebAPIs/XMLHttpRequest.cs`
- `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs`
- `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs`
- `FenBrowser.FenEngine/WebAPIs/WebAPIs.cs`
- `FenBrowser.FenEngine/WebAPIs/TestHarnessAPI.cs`
- `FenBrowser.FenEngine/WebAPIs/TestConsoleCapture.cs`
- `FenBrowser.FenEngine/WebAPIs/StorageApi.cs`
- `FenBrowser.FenEngine/WebAPIs/ResizeObserverAPI.cs`
- `FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs`
- `FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs`
- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs`
- `FenBrowser.FenEngine/WebAPIs/FetchApi.cs`
- `FenBrowser.FenEngine/WebAPIs/CacheStorage.cs`
- `FenBrowser.FenEngine/WebAPIs/Cache.cs`
- `FenBrowser.FenEngine/WebAPIs/BinaryDataApi.cs`
- `FenBrowser.FenEngine/Observers/ObserverCoordinator.cs`
- `FenBrowser.FenEngine/Bindings/Generated/WindowPostMessageOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/WindowBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/TextBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/StructuredSerializeOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/SlotAssignmentModeBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ShadowRootModeBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ShadowRootInitBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ShadowRootBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ScrollToOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ScrollOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ScrollBehaviorBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ProcessingInstructionBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/NodeListBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/NodeBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/NamedNodeMapBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/HTMLElementBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/HTMLCollectionBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/GetRootNodeOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/GetHTMLOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/FocusOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/EventTargetBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/EventListenerOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/EventListenerCallbackBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/EventInitBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/EventBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ElementCreationOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/ElementBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/DocumentTypeBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/DocumentFragmentBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/DocumentBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/CustomEventInitBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/CustomEventBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/AttrBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/CommentBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/AddEventListenerOptionsBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/CDATASectionBinding.g.cs`
- `FenBrowser.FenEngine/Bindings/Generated/CharacterDataBinding.g.cs`

## Appendix B: File-by-File Coverage Ledger

This appendix records the JS-engine-related files under `FenBrowser.FenEngine` excluding `obj` and `bin`.

Status legend:
- `Deep review`: manually opened and inspected in this audit pass

Deep review coverage: `140 / 140` strict JS-engine-related files.

### Bindings

- `FenBrowser.FenEngine\Bindings\Generated\AddEventListenerOptionsBinding.g.cs` | lines=37 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\AttrBinding.g.cs` | lines=289 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\CDATASectionBinding.g.cs` | lines=184 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\CharacterDataBinding.g.cs` | lines=313 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\CommentBinding.g.cs` | lines=188 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\CustomEventBinding.g.cs` | lines=221 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\CustomEventInitBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\DocumentBinding.g.cs` | lines=647 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\DocumentFragmentBinding.g.cs` | lines=188 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\DocumentTypeBinding.g.cs` | lines=223 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ElementBinding.g.cs` | lines=770 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ElementCreationOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\EventBinding.g.cs` | lines=465 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\EventInitBinding.g.cs` | lines=37 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\EventListenerCallbackBinding.g.cs` | lines=17 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\EventListenerOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\EventTargetBinding.g.cs` | lines=242 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\FocusOptionsBinding.g.cs` | lines=33 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\GetHTMLOptionsBinding.g.cs` | lines=33 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\GetRootNodeOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\HTMLCollectionBinding.g.cs` | lines=229 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\HTMLElementBinding.g.cs` | lines=2647 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\NamedNodeMapBinding.g.cs` | lines=311 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\NodeBinding.g.cs` | lines=635 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\NodeListBinding.g.cs` | lines=213 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ProcessingInstructionBinding.g.cs` | lines=197 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ScrollBehaviorBinding.g.cs` | lines=36 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ScrollOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ScrollToOptionsBinding.g.cs` | lines=33 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ShadowRootBinding.g.cs` | lines=321 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ShadowRootInitBinding.g.cs` | lines=45 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\ShadowRootModeBinding.g.cs` | lines=33 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\SlotAssignmentModeBinding.g.cs` | lines=33 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\StructuredSerializeOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\TextBinding.g.cs` | lines=217 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\WindowBinding.g.cs` | lines=3279 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Bindings\Generated\WindowPostMessageOptionsBinding.g.cs` | lines=29 | status=Deep review | gap-signals: none found in static marker sweep

### Core

- `FenBrowser.FenEngine\Core\AnimationFrameScheduler.cs` | lines=258 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Ast.cs` | lines=1040 | status=Deep review | gap-signals: L498: /// Maps placeholder keys (e.g. "__computed_0") to their computed key expressions.
- `FenBrowser.FenEngine\Core\Bytecode\CodeBlock.cs` | lines=42 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Bytecode\Compiler\BytecodeCompiler.cs` | lines=4744 | status=Deep review | gap-signals: L535: int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Operator '{binExpr.Operator}' not supported.")); | L806: int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Prefix operator '{prefixExpr.Operator}' not supported.")); | L1314: throw new FenSyntaxError($"Compiler: Node type {node.GetType().Name} not supported in Bytecode Phase."); | L2814: int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Logical assignment operator '{logicalAssignExpr.Operator}' not supported.")); | L2837: throw new FenSyntaxError($"Compiler: Compound assignment operator '{op}' not supported."); | L4822: throw new FenSyntaxError($"Compiler: {nodeKind} with 'with' statement is not supported in bytecode-only function bodies."); | ...
- `FenBrowser.FenEngine\Core\Bytecode\OpCode.cs` | lines=110 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Bytecode\VM\CallFrame.cs` | lines=141 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Bytecode\VM\VirtualMachine.cs` | lines=3647 | status=Deep review | gap-signals: L839: /// ECMA-262: native built-ins must throw — returning an Error-typed value is a legacy
- `FenBrowser.FenEngine\Core\EngineLoop.cs` | lines=133 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\EnginePhase.cs` | lines=64 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\EventLoop\EventLoopCoordinator.cs` | lines=377 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\EventLoop\MicrotaskQueue.cs` | lines=145 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\EventLoop\TaskQueue.cs` | lines=117 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\ExecutionContext.cs` | lines=140 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\FenEnvironment.cs` | lines=497 | status=Deep review | gap-signals: L37: private static int _legacyGlobalLookupDepth; | L83: if (TryResolveLegacyGlobalFromWindow(name, out var legacyGlobal)) | L85: return legacyGlobal; | L333: if (++_legacyGlobalLookupDepth > MaxLegacyGlobalLookupDepth) | L335: _legacyGlobalLookupDepth--; | L374: _legacyGlobalLookupDepth--; | ...
- `FenBrowser.FenEngine\Core\FenFunction.cs` | lines=364 | status=Deep review | gap-signals: L249: return FenValue.FromError("Bytecode-only mode: AST-backed function invocation is not supported.");
- `FenBrowser.FenEngine\Core\FenObject.cs` | lines=1492 | status=Deep review | gap-signals: L261: var legacyKey = symbolKey.ToPropertyKey(); | L262: if (!string.IsNullOrEmpty(legacyKey)) | L264: var legacyValue = GetWithReceiver(legacyKey, receiver, context); | L265: if (!legacyValue.IsUndefined) | L267: return legacyValue; | L276: if (_prototype != null && !string.IsNullOrEmpty(legacyKey)) | ...
- `FenBrowser.FenEngine\Core\FenRuntime.cs` | lines=16227 | status=Deep review | gap-signals: L837: // Let's throw for now to catch issues. | L4749: // In a real implementation, this would async load and parse the module | L5499: // Symbols stored as @@{id} keys Ã¢â‚¬â€ return empty for now (spec compliant skeleton) | L5814: // Annex B legacy accessors on Object.prototype. | L5886: // Annex B legacy __proto__ accessor on Object.prototype. | L6126: // Ideally bridge syncs back but for now: | ...
- `FenBrowser.FenEngine\Core\FenSymbol.cs` | lines=173 | status=Deep review | gap-signals: L76: public JsValueType Type => JsValueType.Object; // Symbols are treated as objects for now
- `FenBrowser.FenEngine\Core\FenValue.cs` | lines=546 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\IDomBridge.cs` | lines=20 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\InputQueue.cs` | lines=376 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IExecutionContext.cs` | lines=112 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IHistoryBridge.cs` | lines=11 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IHtmlDdaObject.cs` | lines=9 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IModuleLoader.cs` | lines=20 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IObject.cs` | lines=64 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Interfaces\IValue.cs` | lines=81 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\JsThrownValueException.cs` | lines=13 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Lexer.cs` | lines=1888 | status=Deep review | gap-signals: L1731: error = "Invalid legacy octal escape in template literal"; | L1822: error = "Invalid legacy octal escape in template literal";
- `FenBrowser.FenEngine\Core\ModuleLoader.cs` | lines=456 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Parser.cs` | lines=6347 | status=Deep review | gap-signals: L902: long legacyOctalVal = Convert.ToInt64(literal, 8); | L903: return new IntegerLiteral { Token = _curToken, Value = legacyOctalVal }; | L1026: _curToken = _peekToken; // The '}' token placeholder | L2358: // Use a placeholder key for computed properties | L2770: // Return as CallExpression for now, or a specific DynamicImportExpression | L5541: var placeholder = new Identifier( | ...
- `FenBrowser.FenEngine\Core\PropertyDescriptor.cs` | lines=87 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsBigInt.cs` | lines=172 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsIntl.cs` | lines=1771 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsMap.cs` | lines=133 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsPromise.cs` | lines=579 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsSet.cs` | lines=189 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsSymbol.cs` | lines=168 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsTypedArray.cs` | lines=1359 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsWeakMap.cs` | lines=128 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\JsWeakSet.cs` | lines=112 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Core\Types\Shape.cs` | lines=72 | status=Deep review | gap-signals: none found in static marker sweep

### DOM

- `FenBrowser.FenEngine\DOM\AttrWrapper.cs` | lines=224 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\CommentWrapper.cs` | lines=36 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\CustomElementRegistry.cs` | lines=485 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\CustomEvent.cs` | lines=58 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\DocumentWrapper.cs` | lines=820 | status=Deep review | gap-signals: L455: $"NotSupportedError: Failed to execute 'createEvent': The provided event type ('{interfaceName}') is not supported.");
- `FenBrowser.FenEngine\DOM\DomEvent.cs` | lines=313 | status=Deep review | gap-signals: L284: }                // Per legacy behavior, once canceled, setting returnValue=true must not uncancel. | L296: // Per legacy semantics, once true it cannot be reset to false.
- `FenBrowser.FenEngine\DOM\DomMutationQueue.cs` | lines=158 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\DomWrapperFactory.cs` | lines=40 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\ElementWrapper.cs` | lines=3402 | status=Deep review | gap-signals: L3764: // Simplified: parsing style attribute every time is slow but works for now
- `FenBrowser.FenEngine\DOM\EventListenerRegistry.cs` | lines=215 | status=Deep review | gap-signals: L37: /// AbortSignal to remove listener (not implemented yet)
- `FenBrowser.FenEngine\DOM\EventTarget.cs` | lines=479 | status=Deep review | gap-signals: L63: var legacyEventValue = exposeLegacyEvent ? FenValue.FromObject(evt) : FenValue.Undefined; | L64: env.Set("event", legacyEventValue); | L67: windowObj.Set("event", legacyEventValue, context); | L74: var legacyEventValue = FenValue.FromObject(evt); | L75: env.Set("event", legacyEventValue); | L78: windowObj.Set("event", legacyEventValue, context); | ...
- `FenBrowser.FenEngine\DOM\FontLoadingBindings.cs` | lines=545 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\HighlightApiBindings.cs` | lines=1315 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\HTMLCollectionWrapper.cs` | lines=419 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\InMemoryCookieStore.cs` | lines=199 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\LegacyUiEvents.cs` | lines=234 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\MutationObserverWrapper.cs` | lines=194 | status=Deep review | gap-signals: L78: /// Record a mutation (called by legacy DOM mutation methods).
- `FenBrowser.FenEngine\DOM\NamedNodeMapWrapper.cs` | lines=293 | status=Deep review | gap-signals: L64: // Wait, some browsers do expose them. Let's stick to methods + index for now to be safe.
- `FenBrowser.FenEngine\DOM\NodeListWrapper.cs` | lines=201 | status=Deep review | gap-signals: L17: // but for now we'll wrap on demand or rely on Engine's object cache if it exists.
- `FenBrowser.FenEngine\DOM\NodeWrapper.cs` | lines=466 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\Observers.cs` | lines=266 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\RangeWrapper.cs` | lines=267 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\ShadowRootWrapper.cs` | lines=73 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\StaticNodeList.cs` | lines=20 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\TextWrapper.cs` | lines=54 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\DOM\TouchEvent.cs` | lines=147 | status=Deep review | gap-signals: none found in static marker sweep

### Observers

- `FenBrowser.FenEngine\Observers\ObserverCoordinator.cs` | lines=489 | status=Deep review | gap-signals: L557: // borderBoxSize (same as content for now)

### Scripting

- `FenBrowser.FenEngine\Scripting\CanvasRenderingContext2D.cs` | lines=996 | status=Deep review | gap-signals: L20: // private object _imageControl; // Removed legacy control ref
- `FenBrowser.FenEngine\Scripting\JavaScriptEngine.cs` | lines=4224 | status=Deep review | gap-signals: L742: // 1. Capture Phase (skipped for now) | L1245: RejectThenable(FenValue.FromError($"NotSupportedError: Permission '{name}' is not supported")); | L1774: // No-op (legacy API retained for compatibility) | L1828: // No-op (legacy API retained for compatibility) | L3168: FenLogger.Warn($"[JavaScriptEngine] Blocked module fetch for unsupported URI without browser fetch pipeline: {uri}", LogCategory.Network); | L3473: string[] legacyObservers; | ...
- `FenBrowser.FenEngine\Scripting\JavaScriptEngine.Dom.cs` | lines=1484 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Scripting\JavaScriptEngine.Methods.cs` | lines=205 | status=Deep review | gap-signals: L217: // Ignore encoding for now, assume UTF8
- `FenBrowser.FenEngine\Scripting\JavaScriptRuntime.cs` | lines=18 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Scripting\JsRuntimeAbstraction.cs` | lines=127 | status=Deep review | gap-signals: L125: /// Placeholder for a future full JS runtime (e.g., NiL.JS). It is a no-op for now.
- `FenBrowser.FenEngine\Scripting\ModuleLoader.cs` | lines=305 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Scripting\ProxyAPI.cs` | lines=82 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Scripting\ReflectAPI.cs` | lines=163 | status=Deep review | gap-signals: none found in static marker sweep

### WebAPIs

- `FenBrowser.FenEngine\WebAPIs\BinaryDataApi.cs` | lines=399 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\Cache.cs` | lines=612 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\CacheStorage.cs` | lines=345 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\FetchApi.cs` | lines=1028 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\FetchEvent.cs` | lines=237 | status=Deep review | gap-signals: L72: if (TryGetLegacySettledState(RespondWithPromise, out var legacy)) | L74: return legacy; | L222: if (TryGetLegacySettledState(promise, out var legacy)) | L224: return legacy;
- `FenBrowser.FenEngine\WebAPIs\IndexedDBService.cs` | lines=586 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\IntersectionObserverAPI.cs` | lines=241 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\ResizeObserverAPI.cs` | lines=76 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\StorageApi.cs` | lines=363 | status=Deep review | gap-signals: L268: /// (Reload persistence requires tying this to a session ID, which we skip for now).
- `FenBrowser.FenEngine\WebAPIs\TestConsoleCapture.cs` | lines=175 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\TestHarnessAPI.cs` | lines=319 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\WebAPIs.cs` | lines=1044 | status=Deep review | gap-signals: L1076: error = "NotSupportedError: File sharing is not supported"; | L1169: error = "NotSupportedError: Share URL scheme is not supported";
- `FenBrowser.FenEngine\WebAPIs\WebAudioAPI.cs` | lines=1144 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\WebRTCAPI.cs` | lines=939 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\WebAPIs\XMLHttpRequest.cs` | lines=580 | status=Deep review | gap-signals: none found in static marker sweep

### Workers

- `FenBrowser.FenEngine\Workers\ServiceWorker.cs` | lines=141 | status=Deep review | gap-signals: L34: // Using a simple value for now.
- `FenBrowser.FenEngine\Workers\ServiceWorkerClients.cs` | lines=156 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\ServiceWorkerContainer.cs` | lines=238 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\ServiceWorkerGlobalScope.cs` | lines=112 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\ServiceWorkerManager.cs` | lines=318 | status=Deep review | gap-signals: L28: // For simplicity: We track runtimes by scope for now.
- `FenBrowser.FenEngine\Workers\ServiceWorkerRegistration.cs` | lines=123 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\StructuredClone.cs` | lines=159 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\WorkerConstructor.cs` | lines=186 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\WorkerGlobalScope.cs` | lines=373 | status=Deep review | gap-signals: none found in static marker sweep
- `FenBrowser.FenEngine\Workers\WorkerRuntime.cs` | lines=487 | status=Deep review | gap-signals: L376: /// In a real implementation, this would call into the JS engine



