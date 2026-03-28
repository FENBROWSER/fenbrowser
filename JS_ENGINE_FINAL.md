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

It is also not yet Chrome/Firefox-grade. The biggest blocker is architectural, not cosmetic: the runtime still ships two async/promise systems at the same time. That split leaks into `Promise.withResolvers`, `crypto.subtle.digest`, standalone `fetch`, service worker plumbing, and worker event settlement. Chrome and Firefox do not solve this class of problem with parallel promise stacks; they keep one canonical promise/microtask model and force host APIs through it.

The correct reading of the engine in March 2026 is:

- Core execution and event-loop plumbing: materially improved and partly browser-aligned.
- Module system: much stronger than before, but still has browser-divergent resolution shortcuts.
- Worker/service-worker stack: functional, but still compatibility-heavy and not yet lifecycle-clean.
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
- `5` resolved remediation findings: `#15`, `#16`, `#17`, `#18`, `#19`
- `15` active remediation findings: `#1`, `#2`, `#3`, `#4`, `#11`, `#12`, `#13`, `#14`, `#20`, `#21`, `#22`, `#23`, `#24`, `#25`, `#27`
- `2` simulation-program findings: `#28`, `#29`

## Findings

### Finding #1 - The engine still ships two promise architectures at once

Status: Critical gap

Evidence:

- `FenBrowser.FenEngine/Core/Types/JsPromise.cs:13-16`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:17248-17335`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:17799-17890`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:18554-18623`

Resolution:

`JsPromise` is real, but `FenRuntime` still contains `CreateExecutorPromise(...)` and `CreatePromise(...)`, both of which manufacture hand-rolled promise-like objects with `__state`, `__value`, `__reason`, and ad-hoc `then`/`catch`. Chrome and Firefox do not expose two competing promise models inside the same runtime. This is the single most important architectural divergence still active in FenEngine.

Required direction:

Route every host async surface through `JsPromise` only, then delete the legacy promise constructors and legacy state markers.

### Finding #2 - `Promise.withResolvers()` still escapes into the legacy promise path

Status: Critical gap

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:17317-17335`

Assessment:

The modern `CreatePromiseConstructorModern()` uses `JsPromise` for the main constructor and the standard statics, but `Promise.withResolvers()` still calls `CreateExecutorPromise(...)`. That means one of the newest Promise APIs is already wired back into the old compatibility stack.

Required direction:

Make `withResolvers()` allocate a real `JsPromise` capability record and delete the fallback path.

### Finding #3 - `crypto.subtle.digest()` returns the right payload type through the wrong async mechanism

Status: Critical gap

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:12818-12902`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:18554-18623`

Assessment:

`crypto.subtle.digest()` correctly builds an `ArrayBuffer`, but it returns it through `CreatePromise(...)`, not `JsPromise`. The code comment at `FenRuntime.cs:12824-12827` already signals uncertainty about the promise implementation. This is precisely the kind of split Chrome/Firefox avoid.

Required direction:

Keep the digest implementation, replace the settlement path with `JsPromise`, and remove the hand-rolled promise helper.

### Finding #4 - `fetch()` is split between a legacy standalone path and a stronger browser-integrated path

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:12913-12955`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:16762-16794`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:181-186`
- `FenBrowser.FenEngine/WebAPIs/FetchApi.cs:72-110`

Assessment:

Standalone `FenRuntime` still registers `fetch` through `CreateRejectedPromise(...)` and `CreateFetchPromise(...)`, both legacy thenable paths. In the browser host, `JavaScriptEngine.InitRuntime()` immediately overwrites that with `WebAPIs.FetchApi.Register(...)`, which does return `JsPromise`. So the integrated browser path is better than the raw runtime path, but the engine still exposes inconsistent async behavior depending on bootstrap route.

Required direction:

Retire the standalone legacy `fetch` registration and make the runtime-level default use the same `JsPromise`-based fetch implementation.

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

### Finding #11 - Module resolution still contains Node-style `node_modules` lookup

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Core/ModuleLoader.cs:115-124`
- `FenBrowser.FenEngine/Core/ModuleLoader.cs:247-318`

Assessment:

For bare specifiers, `ModuleLoader.Resolve(...)` walks up directories looking for `node_modules`. That is a useful development convenience, but it is not browser-native default module resolution. Chrome and Firefox require browser-side mechanisms such as URL resolution and import maps; they do not silently become Node.

Required direction:

Make Node-style resolution opt-in only, or move it behind explicit tooling/dev-mode switches.

### Finding #12 - Module resolution still falls back to raw unresolved specifiers

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Core/ModuleLoader.cs:126-136`

Assessment:

If resolution fails, `Resolve(...)` can return the original `specifier` string after logging a warning. That keeps the engine moving, but it is too permissive for browser-grade semantics and makes failures harder to reason about.

Required direction:

Fail hard on unresolved module specifiers in browser mode.

### Finding #13 - `FenEnvironment` still resolves unknown names through `document.getElementById`

Status: Critical gap

Evidence:

- `FenBrowser.FenEngine/Core/FenEnvironment.cs:95-100`
- `FenBrowser.FenEngine/Core/FenEnvironment.cs:312-313`
- `FenBrowser.FenEngine/Core/FenEnvironment.cs:362-412`

Assessment:

Missing bindings can still fall back to `document.getElementById(name)` and return an element as though it were a lexical/global binding. Browsers have window named-property behavior, but not by directly grafting `getElementById` lookup into lexical environment resolution. This is a semantic shortcut that will create hard-to-debug edge cases.

Required direction:

Remove this from lexical environment lookup and, if needed, re-implement named access at the correct `Window` / `WindowProxy` layer only.

### Finding #14 - Runtime initialization still duplicates and overrides global registrations

Status: High debt

Evidence:

- `FenBrowser.FenEngine/Core/FenRuntime.cs:4471-4476`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9724-9724`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:11183-11214`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:12701-12712`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:12970-12971`

Assessment:

`Promise`, `queueMicrotask`, and `Intl` are registered more than once in `FenRuntime`, sometimes as stronger implementations and sometimes as fallback objects. This creates override churn and makes it harder to know which surface is canonical at any given point in runtime construction.

Required direction:

Collapse each global to one authoritative registration path.

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

### Finding #20 - Worker script prefetching still discovers `importScripts()` through regex scanning

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:44-49`
- `FenBrowser.FenEngine/Workers/WorkerRuntime.cs:475-483`

Assessment:

`WorkerRuntime` uses `ImportScriptsCallRegex` and `ImportScriptsLiteralUrlRegex` to discover dependency URLs. This is a practical bootstrap shortcut, but it is not parser-driven or browser-grade. It will miss dynamic patterns and can mis-handle comments, strings, or edge-case syntax.

Required direction:

Move worker script dependency discovery to the parser/runtime pipeline instead of regex extraction.

### Finding #21 - `JavaScriptEngine` still carries an apparently dead compatibility layer

Status: High debt

Evidence:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2438-2447`
- Search result: `HandlePhase123Builtins(` appears only at `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2439`

Assessment:

`HandlePhase123Builtins(...)` is still present, but the audit found no call sites in the engine source outside its definition. That makes it compatibility residue, not a live architectural path. Keeping dead execution layers around a browser engine is risky because they preserve outdated assumptions.

Required direction:

Either remove it or reconnect it intentionally with tests. Dead compatibility code should not survive by inertia.

### Finding #22 - `UseMiniPrattEngine` is configuration residue, not a real runtime mode

Status: High debt

Evidence:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1581`
- Search results:
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1581`
  - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs:1631`

Assessment:

The flag still exists and gets set, but the audit did not find an actual second execution pipeline behind it. This is a maintenance liability because it suggests a capability boundary that no longer clearly exists.

Required direction:

Either implement a real alternate engine mode or delete the flag.

### Finding #23 - `RunInline()` is only a wrapper over `Evaluate()`

Status: Medium debt

Evidence:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs:18-45`

Assessment:

`RunInline()` logs, checks sandbox state, and then delegates straight to `Evaluate(code)`. That is acceptable if it is the intended design, but it means any notion of a separate lightweight inline execution path is stale.

Required direction:

Document this as the intended path or remove leftover abstractions that imply a different engine.

### Finding #24 - Several host/browser APIs are still approximations or mocks

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:1419-1423`
- `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:4711-4712`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:6813-6830`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9647-9671`
- `FenBrowser.FenEngine/Core/FenRuntime.cs:9697-9713`
- `FenBrowser.FenEngine/Core/Types/JsIntl.cs:378-379`
- `FenBrowser.FenEngine/Core/Types/JsIntl.cs:662-673`
- `FenBrowser.FenEngine/Core/Types/JsIntl.cs:1216-1225`

Assessment:

Examples found in the current tree:

- `navigator.userAgentData` is a simplified mock.
- Nested `JavaScriptEngine.JsCrypto` still exposes `subtle` as a minimal mock object.
- `window.open` still returns a same-window fallback.
- `matchMedia` is hard-coded around fixed assumptions.
- `requestIdleCallback` returns a synthetic 50 ms `timeRemaining()`.
- `Intl` has explicit simplifications in date-part fallback, CLDR category coverage, and grapheme segmentation.

These are acceptable compatibility layers for now, but they must not be mistaken for browser-grade implementations.

Required direction:

Classify each of these surfaces explicitly as either compatibility shim, simulation, or production implementation, then prioritize replacements accordingly.

### Finding #25 - The parser still favors permissive recovery over strict browser execution semantics

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Core/Parser.cs:2208-2213`
- `FenBrowser.FenEngine/Core/Parser.cs:2770-2777`
- `FenBrowser.FenEngine/Core/Parser.cs:3079-3087`
- `FenBrowser.FenEngine/Core/Parser.cs:5541-5629`

Assessment:

The parser returns partial argument lists, partial expression lists, lowers `import()` to a generic `CallExpression`, and creates placeholder identifiers for destructuring/binding recovery. These choices are useful for resilience and tooling, but they are not the same thing as browser-grade parse and early-error behavior.

Required direction:

Split tooling-friendly recovery from execution-mode parsing more aggressively, or harden the execution path to preserve browser semantics.

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

### Finding #27 - Conformance infrastructure is real, but the visible results still show a large browser gap

Status: High gap

Evidence:

- `FenBrowser.FenEngine/Testing/Test262Runner.cs:337-339`
- `FenBrowser.FenEngine/Testing/Test262Runner.cs:379-381`
- `FenBrowser.FenEngine/Testing/Test262Runner.cs:676`
- `FenBrowser.FenEngine/Testing/Test262Runner.cs:798`
- `FenBrowser.FenEngine/Testing/Test262Runner.cs:839`
- `Results/test262_chunk1_post_direct_eval_fix_20260328/chunk_01.json`
- `Results/test262_full_ramcapped_20260328_154554/state.json`
- `Results/test262_full_ramcapped_20260328_154554/runner.log`
- `docs/test262_ci_baseline.json`

Assessment:

The harness is substantial and resets global event-loop state between tests, but it still runs sequentially (`MaxDegreeOfParallelism = 1`) because of static-state races. The latest visible local artifact in this checkout shows:

- Chunk 1 sample: 582 passed / 418 failed / 1000 total = 58.2% pass rate
- CI baseline snapshot in `docs/test262_ci_baseline.json`: 84 passing / 500 total

That is real progress, but it is still far from Chrome/Firefox parity and confirms that the remaining semantic gaps are not theoretical.

Required direction:

Use Test262 deltas to drive the promise unification, parser hardening, and host API cleanup work first.

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

These findings affect canonical runtime semantics and must be closed first:

- `#1` split promise architecture
- `#2` `Promise.withResolvers()` still on the legacy promise path
- `#3` `crypto.subtle.digest()` still on the legacy promise path
- `#4` split `fetch()` behavior between raw runtime and browser host
- `#13` lexical/global fallback through `document.getElementById`
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

These findings should be tackled immediately after `P0`, or in parallel when the write scopes do not conflict:

- `#11` browser-divergent `node_modules` module resolution
- `#12` silent fallback to raw unresolved specifiers
- `#14` duplicated and overriding global registration in `FenRuntime`
- `#20` regex-based `importScripts()` discovery
- `#25` permissive parser recovery in execution paths
- `#27` conformance gap and static-state limitations in the Test262 path

Required bar for `P1` closure:

- browser-native default semantics
- no silent fallback where resolution or parsing should fail deterministically
- conformance progress demonstrated by repeatable test evidence, not anecdotal site wins

### P2 - Cleanup, host hardening, and residue removal

These findings still matter, but they come after semantic/runtime unification:

- `#21` dead compatibility layer in `JavaScriptEngine`
- `#22` `UseMiniPrattEngine` residue
- `#23` stale `RunInline()` abstraction
- `#24` host API approximations and mocks

Required bar for `P2` closure:

- dead paths removed, not merely ignored
- compatibility surfaces either upgraded to real implementations or explicitly classified as non-production

### Separate subsystem programs

These are not small fixes. They should be treated as full production programs, not as cleanup tasks:

- `#28` WebAudio
- `#29` WebRTC

If they remain compatibility facades, they should continue to be documented as simulations. If they move toward production, they must be implemented as real media/networking subsystems rather than shallow feature-detection shells.

### Order summary

1. Close every `P0` finding fully.
2. Close every `P1` finding with test-backed evidence.
3. Clear `P2` residue so the runtime surface is singular and understandable.
4. Decide whether `#28` and `#29` remain explicit simulations or become full subsystem roadmaps.

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
