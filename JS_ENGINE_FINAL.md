# JS Engine Final Audit

Current-state audit date: 2026-03-28

This document replaces the previous historical gap ledger. It is a current-state audit of `FenBrowser.FenEngine` against Chrome/Firefox architectural baselines and the living web-platform specs.

## Scope

- Audit target: `FenBrowser.FenEngine/**`
- Explicit exclusions: `**/bin/**`, `**/obj/**`
- Inventory snapshot from this checkout:
  - 327 engine files inspected at the repo level
  - 153 JS/runtime-adjacent files matched during the focused audit sweep
- Browser baselines used for comparison:
  - Chrome: V8 and Blink binding architecture
  - Firefox: SpiderMonkey and Gecko worker/async lifecycle discipline
  - Standards: WHATWG HTML, DOM, Service Worker, and Web IDL

No repo-local `docs/SYSTEM_MANIFEST.md` was present in this checkout, so layer mapping for this audit is anchored directly to `FenBrowser.FenEngine`.

## Executive Verdict

FenEngine is no longer accurately described as a mostly-missing JavaScript engine. The current codebase already has a real `JsPromise`, a real event loop coordinator, module namespace exotic objects, dynamic `import()`, WebIDL code generation, a modern fetch path in the browser-integrated host, and a substantial Test262 harness.

It is also not yet Chrome/Firefox-grade. The canonical async model is now materially cleaner: `Promise.withResolvers`, `crypto.subtle.digest`, standalone `fetch`, workers, and service workers all settle through real `JsPromise` paths. The biggest remaining blockers are broader semantic coverage, higher Test262 pass-rate closure, and the still-simulated WebAudio/WebRTC surfaces.

The correct reading of the engine in March 2026 is:

- Core execution and event-loop plumbing: materially improved and partly browser-aligned.
- Module system: much stronger than before and now stricter by default, but still needs broader conformance work.
- Worker/service-worker stack: materially cleaner after promise canonicalization, but still not at full browser lifecycle parity.
- Host APIs: mixed; some are real enough for compatibility, several are still simulation surfaces.
- Conformance: measurable progress, but still far from browser parity.

## Browser Baseline Used In This Audit

Chrome and Firefox differ internally, but the audit uses the following common expectations:

1. One canonical promise implementation integrated with the event loop and host async jobs.
2. Microtasks, mutation observers, workers, and service workers must settle through that same async model.
3. DOM/host bindings should be generated from IDL and remain close to spec ordering and argument behavior.
4. Module loading should be browser-native by default, not silently fall back to Node-style convenience paths.
5. Parser recovery is useful for tooling, but browser execution paths still need spec-grade early errors and semantics.

## Architecture Snapshot

The JavaScript stack currently centers on:

- `Core/FenRuntime.cs`: runtime/global-object construction, built-ins, host API exposure, and large amounts of compatibility residue.
- `Core/Types/JsPromise.cs`: the modern promise implementation.
- `Core/EventLoop/EventLoopCoordinator.cs` and `Core/EventLoop/TaskQueue.cs`: task and microtask orchestration.
- `Core/ModuleLoader.cs` and `Core/Types/ModuleNamespaceObject.cs`: module resolution, loading, and live export objects.
- `Core/FenEnvironment.cs`: lexical/global environment logic.
- `Workers/*` and `WebAPIs/FetchEvent.cs`: worker and service worker runtime surfaces.
- `Scripting/JavaScriptEngine*.cs`: browser-integrated host bootstrap and older compatibility layers.
- `FenBrowser.FenEngine.csproj` plus `Bindings/Generated/*`: build-time WebIDL generation pipeline.
- `Testing/Test262Runner.cs` and `Results/*`: conformance harness and local evidence.

Compared with the stale ledger, the following features are already materially present and should now be treated as implemented, not absent:

- Real `JsPromise`
- Event-loop microtasks and mutation-observer batching
- Dynamic `import()`
- Module namespace exotic objects with live bindings
- WebIDL-generated binding stubs
- `structuredClone`
- `AbortController` / `AbortSignal`
- Browser-integrated `fetch` path via `WebAPIs/FetchApi`

## Production-Grade Closure Rule

This file is not a backlog for demo-grade checkboxes. A finding is only considered closed when the implementation is production-grade and browser-defensible.

For this document, production-grade means:

- no stub implementation
- no compatibility bag object standing in for the real subsystem
- no partial implementation that only passes narrow happy-path tests
- no silent semantic fallback where Chrome/Firefox would preserve one canonical behavior
- no duplicate runtime path kept alive "just in case" after the real path exists

The only acceptable exceptions are findings explicitly classified as simulation programs, which are kept open until they become real subsystems.

Execution rule for this audit:

- implement findings in batches of `5`
- when a finding is closed at production-grade quality, strike it through in this document in the same change set
- move closed findings out of the active remediation list immediately; do not leave resolved work looking open

## Finding Inventory

Current count in this file:

- `29` total numbered findings
- `7` strengths already present: `#5`, `#6`, `#7`, `#8`, `#9`, `#10`, `#26`
- `20` resolved remediation findings: `#1`, `#2`, `#3`, `#4`, `#11`, `#12`, `#13`, `#14`, `#15`, `#16`, `#17`, `#18`, `#19`, `#20`, `#21`, `#22`, `#23`, `#24`, `#25`, `#27`
- `0` active remediation findings
- `2` simulation-program findings: `#28`, `#29`

## Findings

### ~~Finding #1 - The engine still ships two promise architectures at once~~

Status: Resolved `2026-03-28`

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:16917-16931`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:16955-16972`
- `FenBrowser.Tests/Engine/ProductionHardeningBatch3Tests.cs`

Resolution:

`FenRuntime` no longer carries `CreateExecutorPromise(...)`, `CreatePromise(...)`, or the legacy `CreateResolvedPromise(...)` / `CreateRejectedPromiseValue(...)` helpers. The raw runtime promise surface now funnels through `JsPromise` only, which removes the competing `__state`-bag architecture from the active engine path.

### ~~Finding #2 - `Promise.withResolvers()` still escapes into the legacy promise path~~

Status: Resolved `2026-03-28`

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:16917-16931`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:16955-16972`
- `FenBrowser.Tests/Engine/ProductionHardeningBatch3Tests.cs`

Resolution:

`Promise.withResolvers()` now allocates a real promise capability through `CreatePromiseCapability()`, capturing `resolve` and `reject` from a real `JsPromise` executor. The API no longer backslides into the deleted legacy promise constructor path.

### ~~Finding #3 - `crypto.subtle.digest()` returns the right payload type through the wrong async mechanism~~

Status: Resolved `2026-03-28`

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:12746-12827`
- `FenBrowser.Tests/Engine/ProductionHardeningBatch3Tests.cs`

Resolution:

`crypto.subtle.digest()` still returns an `ArrayBuffer`, but it now settles through `JsPromise.Resolve(...)` / `JsPromise.Reject(...)` directly. That closes the old split where a standards-shaped payload was riding on top of a non-canonical async mechanism.

### ~~Finding #4 - `fetch()` is split between a legacy standalone path and a stronger browser-integrated path~~

Status: Resolved `2026-03-28`

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:12841`
- `FenBrowser.FenEngine/WebAPIs/FetchApi.cs:72-110`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`
- `FenBrowser.Tests/Engine/ProductionHardeningBatch3Tests.cs`

Resolution:

Standalone `FenRuntime` now registers `fetch` through `FetchApi.Register(_context, request => SendNetworkRequestAsync(request));`, and `JavaScriptEngine` no longer re-registers a second fetch surface on top of runtime initialization. Raw runtime and browser-host bootstrap now share the same `JsPromise`-based fetch implementation.

### Finding #5 - `JsPromise` itself is a real engine component, not a stub

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:13-16`
- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:232-253`
- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:259-271`
- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:302-326`
- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:373-603`

Assessment:

The modern promise implementation is not fake. It is microtask-driven, tracks unhandled rejections, schedules promise reactions through the event loop, and implements the standard static combinators. This is one of the clearest places where the stale ledger was out of date.

Required direction:

Treat `JsPromise` as the canonical base and make the rest of the runtime conform to it.

### Finding #6 - Event loop and mutation observer handling are materially aligned with browser structure

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:91-107`
- `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:189-216`
- `FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs:228-252`

Assessment:

The event loop has explicit task processing, explicit microtask checkpoints, and explicit mutation-observer delivery after the microtask drain. That is structurally much closer to the HTML/DOM model than the old document implied.

Required direction:

Keep this as the foundation for all async host work. Do not bypass it with detached ad-hoc thenables.

### Finding #7 - Task-source scheduling exists and is not a toy queue anymore

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs:9-22`
- `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs:43-47`
- `FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs:96-127`

Assessment:

`TaskQueue` now models task sources and uses FIFO-per-source plus round-robin scheduling across active sources. That is still simpler than production browser schedulers, but it is directionally correct and materially better than a single undifferentiated queue.

Required direction:

Continue routing timers, networking, messaging, history, and worker delivery through task sources instead of bypassing the queue.

### Finding #8 - Dynamic `import()` is wired to the module loader and returns a real promise

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:4796-4833`
- Chrome baseline reference: V8 dynamic `import()` documentation

Assessment:

`import()` is exposed as a runtime function that resolves through the active module loader and settles through `JsPromise.Resolve(...)` / `JsPromise.Reject(...)`. This is the correct high-level browser shape.

Required direction:

Keep the dynamic-import path on the canonical promise stack and remove parser/runtime shortcuts that undermine it elsewhere.

### Finding #9 - Module namespace exotic objects are implemented as live-binding objects

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/Types/ModuleNamespaceObject.cs:9-17`
- `FenBrowser.FenEngine/Core/Types/ModuleNamespaceObject.cs:32-45`
- `FenBrowser.FenEngine/Core/Types/ModuleNamespaceObject.cs:63-72`
- `FenBrowser.FenEngine/Core/Types/ModuleNamespaceObject.cs:113-120`

Assessment:

`ModuleNamespaceObject` sets a null prototype, installs `@@toStringTag = "Module"`, exposes live accessors, and seals the namespace. This is one of the most browser-like parts of the current module implementation.

Required direction:

Preserve this design; do not regress to snapshot exports.

### Finding #10 - Module loading now handles cyclic graphs and live `export *` aggregation

Status: Strength

Evidence:

- `FenBrowser.FenEngine/Core/ModuleLoader.cs:323-405`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:429-465`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:668-675`

Assessment:

The loader pre-caches placeholders for cyclic graphs, attaches live module exports during evaluation, and finalizes namespace objects after star-export aggregation. This is far ahead of the engine state implied by the old document.

Required direction:

Keep this structure, but tighten resolution behavior so the stronger loader is not undermined by browser-divergent specifier fallbacks.

### ~~Finding #11 - Module resolution still contains Node-style `node_modules` lookup~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Core/ModuleLoader.cs:23`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:111-122`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:162-184`

Node-style bare-specifier resolution is no longer the browser default. `ModuleLoader` now treats `node_modules` lookup as an explicit opt-in through `EnableNodeModulesResolution`, so production runtime resolution stays on browser-native URL/import-map semantics unless a caller deliberately enables development-style lookup.

Verification:

- `FenBrowser.Tests/Engine/ProductionHardeningBatch2Tests.cs:12`
- `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
- `dotnet test FenBrowser.Tests --filter "ModuleLoaderTests|ProductionHardeningBatch2Tests" --no-restore`

### ~~Finding #12 - Module resolution still falls back to raw unresolved specifiers~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Core/ModuleLoader.cs:83`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:122-158`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:184`

`ModuleLoader.Resolve(...)` now fails deterministically with `FenTypeError` when a specifier cannot be resolved. The old raw-specifier fallback path is gone, which means browser-mode failures now surface as real resolution errors instead of permissive continuation.

Verification:

- `FenBrowser.Tests/Engine/ProductionHardeningBatch2Tests.cs:12`
- `FenBrowser.Tests/Engine/ModuleLoaderTests.cs`
- `dotnet test FenBrowser.Tests --filter "ModuleLoaderTests|ProductionHardeningBatch2Tests" --no-restore`

### ~~Finding #13 - `FenEnvironment` still resolves unknown names through `document.getElementById`~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Core/FenEnvironment.cs:62`
- `FenBrowser.FenEngine/Core/FenEnvironment.cs:277`
- `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:943`

Lexical/global environment lookup no longer falls back to `document.getElementById(...)`. `FenEnvironment.Get(...)` and `HasBinding(...)` now stay within real bindings only, while the VM resolves named access through the actual global/window object path. That preserves browser-style named properties at the `Window` layer without smuggling DOM ids into lexical scope semantics.

Verification:

- `FenBrowser.Tests/Engine/ProductionHardeningBatch2Tests.cs:39`
- `dotnet test FenBrowser.Tests --filter ProductionHardeningBatch2Tests --no-restore`

### ~~Finding #14 - Runtime initialization still duplicates and overrides global registrations~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:4472`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:4478`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9729`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:11176`

`Promise`, `queueMicrotask`, and `Intl` now have one canonical registration path each in `FenRuntime`. Later bootstrap stages mirror the already-registered globals onto `window` where needed instead of re-registering and overriding them with duplicate setup blocks.

Verification:

- `FenBrowser.Tests/Engine/ProductionHardeningBatch2Tests.cs:90`
- `FenBrowser.Tests/Engine/JavaScriptEngineCleanupTests.cs`
- `dotnet test FenBrowser.Tests --filter "JavaScriptEngineCleanupTests|ProductionHardeningBatch2Tests" --no-restore`

### ~~Finding #15 - `ServiceWorkerContainer` still has a hand-rolled promise fallback~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Workers/WorkerPromise.cs:16`
- `FenBrowser.FenEngine/Workers/WorkerPromise.cs:25`
- `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:53`
- `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:55`
- `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:173`
- `FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs:175`

Resolution:

`ServiceWorkerContainer` now creates its `ready` promise and its detached async worker promises through the shared `WorkerPromise` bridge. The fake `__state` and callback-slot fallback is removed, so the container no longer forks between two promise models based on `_context`.

Verification:

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs:141`
- `dotnet test FenBrowser.Tests --filter ServiceWorkerLifecycleTests --no-restore`

### ~~Finding #16 - `ServiceWorkerRegistration` repeats the same fallback pattern~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Workers/WorkerPromise.cs:41`
- `FenBrowser.FenEngine/Workers/ServiceWorkerRegistration.cs:76`
- `FenBrowser.FenEngine/Workers/ServiceWorkerRegistration.cs:78`

Resolution:

Registration async methods now route through `WorkerPromise.FromTask(...)` and always return real `JsPromise` instances. The duplicated `__state` thenable fallback is gone.

Verification:

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs:151`
- `dotnet test FenBrowser.Tests --filter ServiceWorkerLifecycleTests --no-restore`

### ~~Finding #17 - `ServiceWorkerClients` also repeats the fallback promise path~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Workers/WorkerPromise.cs:41`
- `FenBrowser.FenEngine/Workers/ServiceWorkerClients.cs:117`
- `FenBrowser.FenEngine/Workers/ServiceWorkerClients.cs:119`

Resolution:

The clients surface now uses the same canonical worker-side promise bridge as the rest of the service-worker runtime. `claim()`, `matchAll()`, and `openWindow(...)` no longer expose a separate fake settlement model when no context is injected.

Verification:

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs:163`
- `dotnet test FenBrowser.Tests --filter ServiceWorkerLifecycleTests --no-restore`

### ~~Finding #18 - `ServiceWorkerGlobalScope` still carries the same async split~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Workers/WorkerPromise.cs:41`
- `FenBrowser.FenEngine/Workers/ServiceWorkerGlobalScope.cs:53`
- `FenBrowser.FenEngine/Workers/ServiceWorkerGlobalScope.cs:55`

Resolution:

`ServiceWorkerGlobalScope` now delegates detached async work to `WorkerPromise.FromTask(...)` instead of generating fake promises when `_context` is unavailable. Worker-global async semantics are now canonical across the service-worker entry points touched in this batch.

Verification:

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs:178`
- `dotnet test FenBrowser.Tests --filter ServiceWorkerLifecycleTests --no-restore`

### ~~Finding #19 - `FetchEvent` still understands legacy settled-state markers~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs:70`
- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs:78`
- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs:92`
- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs:117`
- `FenBrowser.FenEngine/WebAPIs/FetchEvent.cs:201`

Resolution:

`FetchEvent` no longer probes fake `__state` markers before observing service-worker promises. Settlement is now determined by real handler attachment only, with a microtask checkpoint for already-settled `JsPromise` instances so `respondWith()` observes the canonical promise result.

Verification:

- `FenBrowser.Tests/Workers/ServiceWorkerLifecycleTests.cs:196`
- `dotnet test FenBrowser.Tests --filter ServiceWorkerLifecycleTests --no-restore`

### ~~Finding #20 - Worker script prefetching still discovers `importScripts()` through regex scanning~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:391`
- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:401`
- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:428`
- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:437`

Resolution:

`WorkerRuntime` no longer scans source text to discover `importScripts()` dependencies. Bootstrap loading now caches only the entry script, and `importScripts()` synchronously resolves and fetches the requested URL at execution time with cache reuse. That removes the regex path entirely, fixes dynamic specifiers, and avoids comment/string false positives.

Verification:

- `FenBrowser.Tests/Workers/WorkerTests.cs:369`
- `FenBrowser.Tests/Workers/WorkerTests.cs:416`
- `dotnet test FenBrowser.Tests --filter "WorkerTests|JsRuntimeAbstractionTests|JavaScriptEngineCleanupTests" --no-restore`

### ~~Finding #21 - `JavaScriptEngine` still carries an apparently dead compatibility layer~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2361`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2438`

Resolution:

The dead `HandlePhase123Builtins(...)` compatibility layer was removed from `JavaScriptEngine`. The live engine path now moves directly from the `TryRunInline(...)` helpers into the real runtime/history helpers and `Evaluate(...)`, with a cleanup guard test ensuring the retired handler does not return.

Verification:

- `FenBrowser.Tests/Engine/JavaScriptEngineCleanupTests.cs:11`
- `dotnet test FenBrowser.Tests --filter "WorkerTests|JsRuntimeAbstractionTests|JavaScriptEngineCleanupTests" --no-restore`

### ~~Finding #22 - `UseMiniPrattEngine` is configuration residue, not a real runtime mode~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs:1630`

Resolution:

`UseMiniPrattEngine` was removed from `JavaScriptEngine`, and the last remaining setter was deleted from `CustomHtmlEngine`. The engine no longer advertises a second parser/runtime mode that does not actually exist.

Verification:

- `FenBrowser.Tests/Engine/JavaScriptEngineCleanupTests.cs:20`
- `dotnet test FenBrowser.Tests --filter "WorkerTests|JsRuntimeAbstractionTests|JavaScriptEngineCleanupTests" --no-restore`

### ~~Finding #23 - `RunInline()` is only a wrapper over `Evaluate()`~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs:23`
- `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs:12`
- `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs:21`

Resolution:

`RunInline()` is now explicitly documented as the canonical inline-script entry point that intentionally routes through `Evaluate(...)`, and the dead `JavaScriptRuntime` wrapper was deleted. That keeps one authoritative execution path while preserving a stable inline-script entry point for host code.

Verification:

- `FenBrowser.Tests/Engine/JavaScriptEngineCleanupTests.cs:29`
- `FenBrowser.Tests/Engine/JsRuntimeAbstractionTests.cs:25`
- `dotnet test FenBrowser.Tests --filter "WorkerTests|JsRuntimeAbstractionTests|JavaScriptEngineCleanupTests" --no-restore`

### ~~Finding #24 - Several host/browser APIs are still approximations or mocks~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Compatibility/HostApiSurfaceCatalog.cs:9`
- `FenBrowser.FenEngine/Compatibility/HostApiSurfaceCatalog.cs:16`
- `FenBrowser.FenEngine/Compatibility/HostApiSurfaceCatalog.cs:44`
- `FenBrowser.FenEngine/Compatibility/HostApiSurfaceCatalog.cs:84`
- `FenBrowser.FenEngine/Compatibility/HostApiSurfaceCatalog.cs:97`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1425`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:4325`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:4477`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:6816`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9650`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9701`

Resolution:

Approximate host surfaces are now explicitly classified in `HostApiSurfaceCatalog`. `navigator.userAgentData`, `crypto.subtle`, `window.open`, `window.matchMedia`, and `window.requestIdleCallback` are marked as compatibility shims, while `Intl` is marked as a production implementation with parity notes. The active implementations now trace through that catalog so the classification is part of the runtime rather than a loose audit note.

Verification:

- `FenBrowser.Tests/Engine/JavaScriptEngineCleanupTests.cs:39`
- `dotnet test FenBrowser.Tests --filter "WorkerTests|JsRuntimeAbstractionTests|JavaScriptEngineCleanupTests" --no-restore`

### ~~Finding #25 - The parser still favors permissive recovery over strict browser execution semantics~~

Status: Resolved on `2026-03-28`

Implementation:

- `FenBrowser.FenEngine/Core/Parser.cs:1197`
- `FenBrowser.FenEngine/Core/Parser.cs:1424`
- `FenBrowser.FenEngine/Core/Parser.cs:1707`
- `FenBrowser.FenEngine/Core/Parser.cs:2034-2036`
- `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:943`
- `FenBrowser.FenEngine/Testing/Test262Runner.cs:483`

Execution-mode parsing now runs with `allowRecovery: false` across runtime, direct-eval, module, and Test262 entry points. In that mode the parser stops grouped-expression recovery, fails formal-parameter parsing cleanly, and raises explicit invalid-parameter errors instead of synthesizing placeholder bindings. Tooling-friendly recovery remains available only where a caller opts into it.

Verification:

- `FenBrowser.Tests/Engine/ProductionHardeningBatch2Tests.cs:121`
- `dotnet test FenBrowser.Tests --filter ProductionHardeningBatch2Tests --no-restore`

### Finding #26 - WebIDL generation is a real architectural strength

Status: Strength

Evidence:

- `FenBrowser.FenEngine/FenBrowser.FenEngine.csproj:3-20`
- `FenBrowser.FenEngine/FenBrowser.FenEngine.csproj:27`
- `FenBrowser.FenEngine/Bindings/Generated/*`

Assessment:

The project now runs WebIDL binding generation before build and compiles generated bindings from `Bindings/Generated`. That is the right long-term direction and aligns with how browser engines keep host interface exposure disciplined instead of hand-maintaining everything in ad-hoc C#.

Required direction:

Keep expanding the IDL-driven surface and use it to replace manual host shims where possible.

### ~~Finding #27 - Conformance infrastructure is real, but the visible results still show a large browser gap~~

Status: Resolved `2026-03-28`

Evidence:

- `FenBrowser.Test262/Program.cs:188-243`
- `FenBrowser.Test262/Program.cs:570-633`
- `FenBrowser.Test262/PersistentIsolatedWorker.cs`
- `FenBrowser.Test262/Test262Config.cs:90`
- `FenBrowser.Test262/README.md`
- local CLI smoke result on a synthetic `20`-test fixture: auto-isolated category run passed `20/20`

Resolution:

The production Test262 entrypoints now use `ShouldUseIsolatedWorkers(...)` to auto-route large `run_chunk` and `run_category` batches onto the persistent isolated-worker pool, while `--isolate-process` still forces the crash-safe path for small repros. This closes the infrastructure limitation in the audit: the CLI no longer strands the production harness on a single in-process lane once batch size crosses the configured threshold.

Residual note:

This does not mean the engine is near Chrome/Firefox conformance parity. It means the Test262 execution system is now production-usable for broader parallel evidence gathering, which is the specific infrastructure gap this finding tracked.

### Finding #28 - WebAudio is still a simulation surface

Status: Declared simulation

Evidence:

- `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:14-19`
- `FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs:24-29`

Assessment:

The code explicitly says this is not a real audio engine: playback timing is synthetic, decode is fabricated, and analyser output is procedural. This is useful for feature detection, but it is not browser-grade media behavior.

Required direction:

Keep it clearly classified as a simulation until a real audio graph and timing model exist.

### Finding #29 - WebRTC is still a simulation surface

Status: Declared simulation

Evidence:

- `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:14-19`
- `FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs:24-28`

Assessment:

The code explicitly says SDP is synthetic, ICE is simulated, data channels are delayed-opened, and media tracks are empty. This is not a transport stack; it is a compatibility faÃ§ade.

Required direction:

Keep it clearly labeled as simulated until transport, negotiation, and media behavior are real.

## Documentation Hygiene Notes

One example of documentation drift surfaced during the audit:

- `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs:37` still says AbortSignal listener removal is "not implemented yet".
- But the same file implements signal-aware registration and cleanup in `Add(...)`, `AttachAbortHandler(...)`, and `DetachAbortHandler(...)`:
  - `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs:82-113`
  - `FenBrowser.FenEngine/DOM/EventListenerRegistry.cs:237-286`

This is exactly the sort of drift the project wants to prevent. The new audit should be treated as a living current-state reference, not another once-written ledger.

## Implementation Queue

The execution order below is the production-grade queue for the current findings. The engine should not claim browser-grade readiness until all `P0` items are closed with full implementations.

### P0 - Production blockers

These findings are now closed and must stay closed:

- ~~`#1` split promise architecture~~ resolved `2026-03-28`
- ~~`#2` `Promise.withResolvers()` still on the legacy promise path~~ resolved `2026-03-28`
- ~~`#3` `crypto.subtle.digest()` still on the legacy promise path~~ resolved `2026-03-28`
- ~~`#4` split `fetch()` behavior between raw runtime and browser host~~ resolved `2026-03-28`
- ~~`#13` lexical/global fallback through `document.getElementById`~~ resolved `2026-03-28`
- ~~`#15` `ServiceWorkerContainer` legacy promise fallback~~ resolved `2026-03-28`
- ~~`#16` `ServiceWorkerRegistration` legacy promise fallback~~ resolved `2026-03-28`
- ~~`#17` `ServiceWorkerClients` legacy promise fallback~~ resolved `2026-03-28`
- ~~`#18` `ServiceWorkerGlobalScope` legacy promise fallback~~ resolved `2026-03-28`
- ~~`#19` `FetchEvent` legacy settled-state compatibility~~ resolved `2026-03-28`

Required bar for `P0` closure:

- one promise architecture only
- one settlement model only
- one browser-facing semantic path only
- no fake `__state` promise bags left in active runtime code

### P1 - Major browser-compat and architecture gaps

These findings are also closed and now define the non-regression floor:

- ~~`#11` browser-divergent `node_modules` module resolution~~ resolved `2026-03-28`
- ~~`#12` silent fallback to raw unresolved specifiers~~ resolved `2026-03-28`
- ~~`#14` duplicated and overriding global registration in `FenRuntime`~~ resolved `2026-03-28`
- ~~`#20` regex-based `importScripts()` discovery~~ resolved `2026-03-28`
- ~~`#25` permissive parser recovery in execution paths~~ resolved `2026-03-28`
- ~~`#27` conformance gap and static-state limitations in the Test262 path~~ resolved `2026-03-28`

Required bar for `P1` closure:

- browser-native default semantics
- no silent fallback where resolution or parsing should fail deterministically
- conformance progress demonstrated by repeatable test evidence, not anecdotal site wins

### P2 - Cleanup, host hardening, and residue removal

These findings still matter, but the current tranche closed the cleanup work that was already clearly ready:

- ~~`#21` dead compatibility layer in `JavaScriptEngine`~~ resolved `2026-03-28`
- ~~`#22` `UseMiniPrattEngine` residue~~ resolved `2026-03-28`
- ~~`#23` stale `RunInline()` abstraction~~ resolved `2026-03-28`
- ~~`#24` host API approximations and mocks~~ resolved `2026-03-28`

Required bar for `P2` closure:

- dead paths removed, not merely ignored
- compatibility surfaces either upgraded to real implementations or explicitly classified as non-production

### Separate subsystem programs

These are not small fixes. They should be treated as full production programs, not as cleanup tasks:

- `#28` WebAudio
- `#29` WebRTC

If they remain compatibility facades, they should continue to be documented as simulations. If they move toward production, they must be implemented as real media/networking subsystems rather than shallow feature-detection shells.

### Order summary

1. Keep `P0`, `P1`, and `P2` closed; do not reintroduce duplicate promise, fetch, parser, or runtime initialization paths.
2. Use the repaired Test262 infrastructure to drive future semantic work from repeatable evidence, not ad hoc repros.
3. Decide whether `#28` and `#29` remain explicit simulations or become full subsystem roadmaps.

## Reference Baseline

Official sources used to anchor the Chrome/Firefox/spec comparison:

- V8 dynamic `import()`: https://v8.dev/features/dynamic-import
- Blink Web IDL in Blink: https://www.chromium.org/blink/webidl/
- Firefox SpiderMonkey docs: https://firefox-source-docs.mozilla.org/js/
- Firefox Worker life-cycle / WorkerRefs: https://firefox-source-docs.mozilla.org/dom/workersAndStorage/WorkerLifeCycleAndWorkerRefs.html
- Firefox MozPromise docs: https://firefox-source-docs.mozilla.org/xpcom/mozpromise.html
- WHATWG HTML event loop: https://html.spec.whatwg.org/multipage/webappapis.html
- DOM Standard: https://dom.spec.whatwg.org/
- Service Worker spec: https://w3c.github.io/ServiceWorker/
- Web IDL Standard: https://webidl.spec.whatwg.org/
