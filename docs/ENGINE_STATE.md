# FenBrowser — Engine State

> Auto-generated Gate 0 Reality Audit. Last refreshed: 2026-06-27.
> Status taxonomy: `IMPLEMENTED` | `PARTIAL` | `STUBBED` | `BROKEN` | `UNKNOWN` | `DEPRECATED` | `DESIGN_INTENT`

## 1. FenBrowser.Js — JavaScript Engine

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| Lexer | IMPLEMENTED | 98.4% test262 parser subset |
| Parser (incl. AST) | IMPLEMENTED | Full ES2025 syntax; Annex B; class fields, private fields, static blocks; import/export; optional chaining; nullish coalesce; destructuring; async/await; generators |
| AstValidation | IMPLEMENTED | Early-error validation wired; destructuring target validation |
| BytecodeCompiler | IMPLEMENTED | All ES2025 opcodes; generator suspend/resume; class compilation; private fields |
| BytecodeVerifier | IMPLEMENTED | Stack balance, type safety checks |
| BytecodeInterpreter | IMPLEMENTED | Full execution pipeline; call/construct; generators; spread; dynamic import |
| Runtime (JsValue, JsIsolate, JsRealm) | IMPLEMENTED | Realms, isolates, handle scopes, string interning |
| Objects (JsObject, property descriptors) | IMPLEMENTED | Full [[Get]]/[[Set]]/[[DefineOwnProperty]]; Proxy; accessors |
| Heap (JsHeap, GC) | IMPLEMENTED | Mark-sweep; generational nursery; remembered set; write barriers; GC-stress mode |
| Builtins | IMPLEMENTED | Full surface for Array, Object, String, RegExp, Function, Promise, Map, Set, WeakMap, WeakSet, Symbol, Proxy, Reflect, Date, Math, JSON, Error, TypedArrays, DataView, ArrayBuffer, Atomics, BigInt, Intl (partial), Temporal (partial), Iterator helpers (partial) |
| Promises | IMPLEMENTED | PromiseJob queue; microtask checkpoint; all combinators (all/allSettled/any/race) |
| Modules | PARTIAL | Parse + import/export binding works; module linking/environment records stubbed (no real module graph resolution); dynamic import returns rejected Promise |
| Regex engine | PARTIAL | Dual backend: native VM for .test() + .NET fallback for exec/match; unicode property escapes; lookbehind; named groups; hasIndices |
| Intl | PARTIAL | DateTimeFormat, NumberFormat, Collator backed by .NET globalization; Locale, RelativeTimeFormat, PluralRules, ListFormat, DurationFormat, Segmenter, DisplayNames partial |
| Temporal | PARTIAL | ISO calendar full; non-ISO calendars (gregory, buddhist, roc, japanese, coptic, ethiopic, indian, islamic, hebrew, chinese, dangi); ZonedDateTime on epoch ns; Duration round/total; since/until partial; relativeTo/DST gaps remain |
| Host hooks (IHostHooks) | IMPLEMENTED | HostObjectTable; StandaloneHostHooks; NavigationEpoch; DocumentEpoch |
| JIT compiler | PARTIAL | Baseline JIT with IL expression-tree codegen; inline caches for GetPropByName/Call/GetElem; not yet enabled by default |
| Fuzzing | IMPLEMENTED | FenBrowser.Js.Fuzz project |

**Overall JS engine**: 92.07% test262 (50842/55220). ~4378 failures, top clusters: staging(566), RegExp(343), Temporal(268), eval-code(211), TypedArray(166), class(166), TypedArrayCtors(154), intl402 NumberFormat(140), Temporal-intl(138), DateTimeFormat(122).

## 2. FenBrowser.Core — Platform Primitives

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| DOM V2 (Node, Element, Document) | IMPLEMENTED | Full tree model; attributes; classList; dataset; querySelector/querySelectorAll; matches; closest; live collections; TreeWalker; Range; MutationObserver |
| EventTarget / Events | IMPLEMENTED | addEventListener/removeEventListener; capture/bubble; stopPropagation; preventDefault; CustomEvent; MouseEvent; KeyboardEvent; InputEvent; FocusEvent |
| Shadow DOM | PARTIAL | ShadowRoot present; slot assignment basic; closed/open mode; not fully tested |
| Custom Elements | PARTIAL | CustomElementRegistry present; upgrade path; not fully tested |
| HTML Parser | IMPLEMENTED | Full WHATWG tokenizer + tree builder; StreamingHtmlParser; PreloadScanner; script insertion points; template parsing |
| CSS Types (CssComputed, etc.) | IMPLEMENTED | Full CSSOM value model; computed style cache |
| Selector Engine | IMPLEMENTED | Full CSS selectors level 4; specificity calculation |
| Network (NetworkClient, fetch, XHR) | PARTIAL | HttpClient-based; handler pipeline (CSP, CORS, HSTS, AdBlock, SafeBrowsing, TrackingPrevention); WhatwgUrl full state machine; MimeSniffer; EncodingSniffer; ResourcePrefetcher; SecureDnsResolver |
| CORS | PARTIAL | Basic enforcement; preflight partial; credentials mode partial |
| Cookies | PARTIAL | PartitionedCookieStore exists; same-site behavior basic; not fully tested |
| Storage | PARTIAL | localStorage, sessionStorage skeletons; IndexedDB design intent; quota design intent |
| Security (CSP, CORB, sandbox) | PARTIAL | CspPolicy; CorbFilter; OopifPlanner; sandbox policies; AttributeSanitizer |
| Accessibility | IMPLEMENTED | Full A11y tree; ARIA role resolution; AccName/AccDesc calculation; platform bridges (UIA, AT-SPI2, NSAccessibility) |
| Memory (ArenaAllocator) | IMPLEMENTED | Unsafe bump-pointer; FrameArenaPool; canary+poison in DEBUG; EngineMetrics; TimelineTracer; FrameBudgetMonitor; JankDetector |
| WebIDL | PARTIAL | WebIdlParser (recursive descent); WebIdlBindingGenerator (C# code gen); .idl files exist for core interfaces; not all APIs have generated bindings |
| WebDriver | PARTIAL | W3C WebDriver protocol; command handler; window commands; element commands; shadow root commands; script commands; cookie commands. **16/582 browser tests failing (5 WebDriver)** |
| Process Isolation | PARTIAL | BrokeredProcessIsolationCoordinator; NetworkProcessIpc; GpuProcessIpc; UtilityProcessIpc; IPC fuzzing harness; shared memory frame delivery |

## 3. FenBrowser.FenEngine — Layout & Rendering

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| Layout Engine | IMPLEMENTED | BoxTreeBuilder; block, inline, flex, grid, table, float, absolute/fixed/sticky positioning; margin collapse; text measurement |
| Formatting Contexts | IMPLEMENTED | Block, Inline, Flex, Grid, Table, Float, AbsolutePosition |
| Text Layout | IMPLEMENTED | HarfBuzz text shaping; font metrics normalization; line breaking |
| Rendering (Paint) | IMPLEMENTED | SkiaSharp paint pipeline; stacking contexts; clipping; opacity; transforms; backgrounds; borders; images; text painting; canvas |
| CSS Engine | IMPLEMENTED | CascadeEngine; custom properties; container queries; media range queries; selector matching; specificity; inheritance; computed values |
| Style Invalidation | PARTIAL | Dirty flag system; not fully incremental |
| Compositing | PARTIAL | DamageTracker; BaseFrameReusePolicy; FrameBudgetAdaptivePolicy; display lists |
| SVG Rendering | IMPLEMENTED | ISvgRenderer adapter; sandboxed (max recursion 32, max filters 10, max render time 100ms) |
| Scripting (BrowserScriptEngineRuntime) | IMPLEMENTED | FenJS integration; DOM bridging via HostObjectTable; BrowserFenJsHostHooks; FenJsMutationObserverHost |
| Event Loop | IMPLEMENTED | EventLoopCoordinator; TaskQueue; MicrotaskQueue; rAF callbacks; mutation observer callbacks; delayed tasks |
| Web APIs | PARTIAL | Fetch, XHR, WebStorage, IndexedDB (stub), Cache/CacheStorage (stub), WebAudio (stub), WebRTC (stub), IntersectionObserver, ResizeObserver |
| Workers | PARTIAL | WorkerRuntime exists; not fully tested |

**Real-site rendering quality** (from existing diagnostic dumps):
- HackerNews: 777/816 elements get layout rects (95%) — **best result**
- React docs: 1240/1843 elements get layout rects (67%) — significant gaps
- GitHub: 1090/1959 elements get layout rects (56%); 194 zero-area boxes; 102 with content but zero height — **inline/flex layout gaps**
- x.com: 10/81 elements get layout rects (12%) — **scripts barely executing**

## 4. FenBrowser.Host — Browser Shell

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| Program.cs startup | IMPLEMENTED | --headless, --test262, --wpt, --acid2 modes |
| BrowserIntegration | IMPLEMENTED | Connects BrowserHost to render loop; double-buffered display list; event queue; engine thread |
| Window/UI Integration | IMPLEMENTED | Silk.NET+OpenGL+Skia windowing stack; coordinate system mapping |
| Input Routing | IMPLEMENTED | Mouse, keyboard, scroll event dispatch |
| Chrome/Chromium Management | PARTIAL | ChromeManager for multi-process |
| DevTools | IMPLEMENTED | In-process Skia DevTools UI; CDP remote debug server on port 9222; Elements, CSS, Box Model panels; DomDomain, CSSDomain, RuntimeDomain |
| Tooling (FenBrowser.Tooling) | IMPLEMENTED | WPT runner integration; diagnostic commands; build/dev tooling |

## 5. Test Infrastructure

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| test262 runner (FenBrowser.Js.Test262) | IMPLEMENTED | Path-based CLI; batched execution; timeout enforcement; result JSON with failure details |
| Browser unit tests (FenBrowser.Tests) | IMPLEMENTED | 582 tests, 566 pass, 16 fail |
| JS engine unit tests (FenBrowser.Js.Tests) | IMPLEMENTED | 758 tests, 752 pass, 6 fail |
| Core unit tests (FenBrowser.Core.Tests) | IMPLEMENTED | Present but needs baseline run |
| WPT runner | PARTIAL | Upstream wptrunner + wptrunner_fenbrowser plugin; WebDriver-based; dom/lists 180/189 (95.2%) |
| Fuzzing (FenBrowser.Js.Fuzz) | IMPLEMENTED | JS engine fuzzing |
| Differential testing (FenBrowser.Js.Compare) | IMPLEMENTED | Comparison vs reference engine |
| Conformance (FenBrowser.Conformance) | IMPLEMENTED | Present but needs baseline |

## 6. Diagnostic Infrastructure (GATE 1)

| Subsystem | Status | Evidence |
|-----------|--------|----------|
| debug-site command | IMPLEMENTED | `FenBrowser.Tooling -- debug-site <url>` produces complete trace bundle |
| Trace bundle output | IMPLEMENTED | 20+ artifacts per run; `logs/real-site/<site>/<run-id>/` |
| Script loading trace | IMPLEMENTED | `BrowserScriptLoadingSnapshot` with per-script lifecycle records |
| Event loop trace | IMPLEMENTED | `BrowserEventLoopSnapshot` with DCL/load/microtask/timer/rAF counters |
| Missing API tracker | IMPLEMENTED | `MissingApiTracker` — deduplicated per-site JSON with EngineLog integration |
| Engine capabilities registry | IMPLEMENTED | `EngineCapabilities` — thread-safe HTML/CSS/JS feature tracking |
| Network capture | IMPLEMENTED | `DebugSiteNetworkCapture` — request/response metadata with timing |
| Lifecycle tracking | IMPLEMENTED | `NavigationLifecycleTransition` events with phase timeline |
| Style/layout/paint dumps | IMPLEMENTED | Computed style dump, layout box dump, paint tree dump, display list dump |
| Screenshot capture | IMPLEMENTED | 1280x800 Skia-rendered screenshot in bundle |
| Structured logging | IMPLEMENTED | `EngineLog` with NDJSON + trace sinks; `EngineFailureBundleExporter` |
| IPC trace artifact | NOT_STARTED | ipc.json not yet generated in trace bundle |
| Sandbox denial artifact | NOT_STARTED | sandbox_denials.json not yet generated |
| Performance artifact | NOT_STARTED | performance.json not yet generated (telemetry data available) |

## 7. Known Architecture Issues

1. **No per-tab renderer process isolation** — single monolithic process (FEN_PROCESS_ISOLATION=brokered env var exists but not default)
2. **No real network broker** — NetworkClient runs in-process
3. **No out-of-process image decoding** — hostile images can crash renderer
4. **Module linking not implemented** — dynamic import and module graphs don't resolve
5. **Missing Web API tracking** — `MissingApiTracker` exists (deduplicated per-site JSON, EngineLog integration with `LogMarker.Unimplemented`); `EngineCapabilities` thread-safe registry for HTML/CSS/JS features
6. **Script loading diagnostics** — `BrowserScriptLoadingSnapshot` captures per-script discovery/fetch/execute lifecycle; trace events: ScriptDiscovered/ScriptFetchStarted/ScriptFetchCompleted/ScriptReady/ScriptExecutionStarted/ScriptExecutionCompleted/ScriptExecutionFailed
7. **Structured trace bundle output** — `debug-site` command produces full bundle per PLAN.MD spec in `logs/real-site/<site>/<run-id>/` with 20+ artifacts (summary.md, trace.jsonl, console.log, network.json, exceptions.json, missing_apis.json, script_loading.json, event_loop.json, style_layout.json, style_dump.txt, layout_dump.txt, paint_dump.txt, display_list.txt, screenshot.png, lifecycle.json, lifecycle_timeline.json, probes.json, artifact_manifest.json). Gaps: ipc.json, sandbox_denials.json, performance.json not yet generated.
