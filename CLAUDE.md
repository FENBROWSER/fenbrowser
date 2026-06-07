# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⚠️ Current Reality (read first — the tree has diverged from older docs)

Much of this file and the `docs/` Volumes describe the original architecture. The repo has since pivoted; these facts override anything below that conflicts:

- **Target framework is `net10.0`** (not net8.0) across all projects.
- **The active JavaScript engine is `FenBrowser.Js`** — a standalone tree-walking/bytecode engine (`Lexer → Parser → Ast → BytecodeCompiler → BytecodeVerifier → BytecodeInterpreter`). This is where all current JS conformance work happens. The legacy JS engine inside `FenBrowser.FenEngine/Core/Bytecode/` (the `FenRuntime`/`VirtualMachine`/`FenValue` API) is **no longer the focus** — ignore it for JS work unless explicitly told otherwise.
- **`FenBrowser.WPT` and `FenBrowser.Test262` projects no longer exist.** Test262 now runs through **`FenBrowser.Js.Test262`** with a completely different CLI (see Test262 section). There is no in-tree WPT runner at present.
- **New projects**: `FenBrowser.Js` (engine), `FenBrowser.Js.Tests` (xUnit), `FenBrowser.Js.Test262` (conformance runner), `FenBrowser.Js.Shell` (REPL/CLI), `FenBrowser.Js.Compare` (differential vs. a reference engine), `FenBrowser.Js.Fuzz` (fuzzing), `FenBrowser.Core.Tests`, `FenBrowser.Tooling`.
- **JS resume protocol**: all FenJS revamp work resumes from `.fenjs-progress.md` at the repo root — never restart from step 1.
- test262 root on this machine: `C:\Users\udayk\Videos\test262`.

## Documentation Index (Read Before Modifying Subsystems)

The `docs/` folder is the authoritative per-subsystem encyclopedia. **Before modifying any major subsystem, read the relevant Volume** — they contain exact line ranges for key methods, saving multiple searches.

| Volume | File | What it covers |
|--------|------|----------------|
| I | `docs/VOLUME_I_SYSTEM_MANIFEST.md` | High-level architecture; process model (`FEN_PROCESS_ISOLATION=brokered`); navigation lifecycle state machine; HTML parsing baselines; CSS/Layout/Paint baseline changelogs; build resolver notes |
| II | `docs/VOLUME_II_CORE.md` | **Per-file line maps for every key method in `FenBrowser.Core`**: DOM V2 (Node, Element, Document, EventTarget, MutationObserver, Range), Parsing (HtmlTokenizer, HtmlTreeBuilder, StreamingHtmlParser), Network (ResourceManager, NetworkClient, MimeSniffer), CSS types, security, selector engine |
| III | `docs/VOLUME_III_FENENGINE.md` | Layout pipeline (BoxTreeBuilder → Measure/Arrange), formatting contexts, rendering pipeline, scripting engine; recent hardening with exact file paths |
| IV | `docs/VOLUME_IV_HOST.md` | Host entry point (`Program.cs` startup modes); Silk.NET+OpenGL+Skia windowing stack; `BrowserIntegration` coordinate systems (Window/UI/Document space); input routing |
| V | `docs/VOLUME_V_DEVTOOLS.md` | In-process Skia DevTools UI; CDP remote debug server on port **9222**; Elements panel, CSS inspection, box model |
| VI | `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md` | WebDriver server architecture; WPT/Test262/Acid2 verification ecosystem |

Other useful docs:
- `docs/SPEC_EVENT_LOOP.md` — authoritative Task/Microtask/Render execution order spec (read before touching `EventLoop`, `MicrotaskQueue`, `TaskQueue`)
- `docs/COMPLIANCE.md` — current compliance scores per subsystem and known half-baked features
- `docs/GLOSSARY.md` — canonical definitions (Box, Bridge, Dirty Flag, Microtask, etc.)
- `docs/THIRD_PARTY_DEPENDENCIES.md` — all external libraries and licenses

Key facts from Volume I not visible in code:
- **Startup modes**: `--headless`, `--test262`, `--wpt`, `--acid2` (passed to `FenBrowser.Host/Program.cs`)
- **Process isolation**: set env `FEN_PROCESS_ISOLATION=brokered` to enable multi-process mode
- **Diagnostics dir**: override with `FEN_DIAGNOSTICS_DIR` env var
- **DevTools CDP port**: 9222

## Engineering Rules (Non-Negotiable)

From `docs/ENGINEERING_CONSTITUTION.md` — violating these is a hard block:

1. **Layout authority lives only in FenEngine.** HarfBuzz shapes glyphs; Skia draws pixels. Neither decides line breaks, line height, or box sizing. Use `ITextMeasurer` / `ISvgRenderer` adapters — never call `TextBlock.MaxWidth` or raw `Svg.Skia` directly.
2. **Never use raw `SKFontMetrics` in layout code.** Normalize to `NormalizedFontMetrics` (Ascent, Descent, LineHeight, XHeight). FenEngine calculates line-height; Skia only informs.
3. **SVG must be sandboxed**: max recursion depth 32, max filters 10, max render time 100 ms, external references disabled. SVG failures degrade to a placeholder — never crash.
4. **No `SKCanvas` outside `IRenderBackend`.** Rendering backend must stay abstract (testable via `HeadlessRenderBackend`, swappable without touching layout).
5. **Wrap risky dependencies** (`RichTextKit`, `Svg.Skia`) behind interfaces. All new hot-path code must survive the dependency dying.

**Banned patterns** (CI will fail):
- `TextBlock.MaxWidth` — RichTextKit deciding layout
- Raw `SKFontMetrics` in layout code
- `SKCanvas` outside `IRenderBackend`
- Direct `Svg.Skia` calls outside `ISvgRenderer` adapter
- Direct `RichTextKit` calls outside `ITextMeasurer` adapter

## Definition of Done

From `docs/DEFINITION_OF_DONE.md` — check before finishing any implementation:

**Tier 0 (all changes):**
- `dotnet build` exits 0, zero warnings on new code
- `dotnet test` exits 0, no newly-failing tests
- No `Console.WriteLine`, `TODO REMOVE`, or commented-out dead code on hot paths

**Tier 1 (feature/bug-fix):**
- Cite normative spec in comment or PR (WHATWG/ECMA-262/RFC section + algorithm name)
- Relevant WPT or Test262 tests pass with no regressions
- Security analysis written for any change touching: IPC, parsers, sandbox, origins/CORS, unsafe memory
- If adding/modifying a **parser or IPC message type**: update `IpcFuzzHarness`/`StructuredMutator` and run 10 000 fuzzing iterations

**Tier 2 (architecture changes):** no >5% benchmark regression; arena slab high-water mark stable; `docs/VOLUME_*.md` updated.

## Commit & Push Discipline (Non-Negotiable)

Ship work in **feature/component-sized units**, one at a time. For each unit, complete this loop **before starting the next**:

1. **Clean build** — `dotnet build` of the affected project(s) must succeed with **zero errors**.
2. **Verify** — run the relevant verification: **test262**, **WPT**, or **local unit tests** (whichever exercises the change). It must pass with no new failures. (When the change is in the JS engine, prefer a test262 slice + `FenBrowser.Js.Tests`.)
3. **Commit only if verification passed** — write **human-style** commit messages and split work into the **smallest coherent chunks** (one logical change per commit, e.g. a fix and its unrelated diagnostic land as separate commits).
4. **Push** the committed work to the remote.
5. **Then** move on to the next feature/component.

Hard rules:
- Never commit code that did not build or was not verified.
- Never batch multiple unrelated features/fixes into one commit.
- Never leave a completed, verified unit unpushed before starting the next one.
- Do **not** add AI/co-author trailers to commit messages — commits read as authored by a human.

## Build Commands

```bash
# Build entire solution (net10.0)
dotnet build FenBrowser.sln -c Release

# JS engine work (the common case) — build engine + its tests/runner
dotnet build FenBrowser.Js/FenBrowser.Js.csproj -c Release
dotnet build FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj -c Release
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release

# Browser-engine projects
dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release
dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Release
dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Release

# Note: FenBrowser.Js enables TreatWarningsAsErrors + Nullable=enable — new engine
# code must be warning-clean and null-annotated or the build fails.
# FenEngine build auto-runs WebIDL binding generation before compile
# (WebIdlGen reads FenBrowser.Core/WebIDL/Idl/*.idl → writes FenBrowser.FenEngine/Bindings/Generated/)
```

## Running Tests

```bash
# JS engine unit tests (the primary suite for JS work)
dotnet test FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj
dotnet test FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj --filter "FullyQualifiedName~ArrayIsArrayTests"
dotnet test FenBrowser.Js.Tests/FenBrowser.Js.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"

# Browser-engine unit tests
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj
dotnet test FenBrowser.Core.Tests/FenBrowser.Core.Tests.csproj
```

> The old `FenBrowser.WPT` runner no longer exists in the tree. There is currently no in-tree WPT runner.

## Test262 Conformance Testing Protocol

Test262 runs through the **`FenBrowser.Js.Test262`** runner. The runner takes a **directory or file path** (not "chunk numbers") and walks it.

**Per-test timeout — MANDATORY, NO EXCEPTIONS**: Every test262 run MUST pass `--timeout-ms 2000`. Any test running longer than 2s is skipped, never awaited — a single test must never hang or block the whole suite. This applies to *every* invocation — quick categories, full subtrees, single reruns.

```bash
# Build once
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release
EXE=./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe
ROOT="C:/Users/udayk/Videos/test262"

# Run a category / subtree (runtime semantics). --test262 takes a path under $ROOT.
$EXE --runtime-subset --root "$ROOT" --test262 "$ROOT/test/built-ins/Array" \
     --max 100000 --timeout-ms 2000 --out Results/test262/array.json

# Run a single file
$EXE --runtime-subset --root "$ROOT" --test262-file "<abs path to .js>" --timeout-ms 2000 \
     --out Results/test262/single.json

# Parser-only conformance (no execution)
$EXE --parser-subset --root "$ROOT" --test262 "$ROOT/test/language" --timeout-ms 2000 --out Results/test262/parse.json

# Other modes: --list, --dry-run, --dashboard, --verify-gates
# Other flags: --features a,b,c | --supported-features a,b,c | --expectations <path> | --engine <name>
```

**Full ~53k-test suite** — use the memory-safe batched script (one OS process per directory batch so RAM is
released between batches, aggregating into `Results/test262/batched/_batched_total.json`):

```bash
bash run_full_batched.sh           # at repo root
# Or a single process over the whole tree (heavier on RAM):
$EXE --runtime-subset --root "$ROOT" --test262 "$ROOT/test" --max 1000000 --timeout-ms 2000 \
     --out Results/test262/full.json
```

Result JSON fields: `passed`, `total`, and `failures[].details` (the real thrown message — *not* `tests[].message`).

### Execution & Results Policy (MANDATORY)
- **Category by category** — drive one category at a time, not the whole suite blindly.
- **95% gate** — a category is done at **≥95% pass rate**; only then move to the next. Stay on a category (localize-and-fix) until it clears 95%.
- **Results layout** under `Results/test262/`:
  - `Results/test262/full/` — full-suite run results.
  - `Results/test262/categories/` — per-category run results.
- **Clear stale results; keep only one day of history** — purge result files older than 24h before/after runs so the folders hold only the latest day. (`Results/` is gitignored — local housekeeping, never committed.)

### Memory safety
- Check RAM before large runs; prefer `run_full_batched.sh` (process-per-batch) over a single whole-tree process.
- If a run crashes, retry once before moving on. Stale `Results/` files are gitignored local housekeeping.

## Architecture Overview

FenBrowser is a browser engine + standalone JS engine written in C#/.NET 10.

### Project Dependency Graph
```
FenBrowser.Js            ← standalone JS engine (lexer/parser/bytecode/interpreter) — ACTIVE JS WORK
    ↑
FenBrowser.Js.Tests      ← xUnit tests for the JS engine
FenBrowser.Js.Test262    ← ECMAScript Test262 conformance runner (path-based CLI)
FenBrowser.Js.Shell      ← JS REPL / script CLI
FenBrowser.Js.Compare    ← differential testing vs. a reference engine
FenBrowser.Js.Fuzz       ← JS engine fuzzing

FenBrowser.Core          ← foundational types, DOM, CSS interfaces, networking
    ↑
FenBrowser.FenEngine     ← layout, rendering, WebAPIs, legacy JS engine (depends on Core)
    ↑
FenBrowser.Host          ← process orchestration, BrowserIntegration, IPC coordinators
FenBrowser.DevTools      ← CDP-like debug server (DOM/CSS/Runtime domains)
FenBrowser.WebDriver     ← WebDriver protocol (W3C)
FenBrowser.Core.Tests    ← xUnit tests for Core
FenBrowser.Tests         ← xUnit tests (references Core, FenEngine, DevTools, Host)
FenBrowser.Tooling       ← dev/build tooling
FenBrowser.WebIdlGen     ← WebIDL parser → C# binding code generator
```

### FenBrowser.Js (active JavaScript engine)
Standalone, dependency-light ECMAScript engine. Pipeline: source → lexer → parser → AST → bytecode → interpreter. Key subsystems (all under `FenBrowser.Js/`):
- **Lexer** (`Lexer/`) and **Parser** (`Parser/`) → **Ast** (`Ast/`), with **AstValidation** (`AstValidation/`)
- **Bytecode** (`Bytecode/`): `BytecodeCompiler`, `BytecodeVerifier`, the bytecode/opcode definitions
- **Interpreter** (`Interpreter/`): `BytecodeInterpreter` (+ `BytecodeInterpreter.Operators.cs`), `JsThrownException`
- **Runtime** (`Runtime/`): `JsValue`/`JsValueTag`, `JsIsolate`, `JsRealm`, handle types (`ObjectHandle`, `StringHandle`, `SymbolHandle`), `StringInterner`, `CspPolicy`
- **Objects** (`Objects/`): `JsObject` and the object model; **Heap** (`Heap/`): `JsHeap`, handle scopes, GC; **Environments** (`Environments/`): scopes/bindings
- **Builtins** (`Builtins/`): all `globalThis` built-ins (Array, Object, String, RegExp, Map/Set, Promise, TypedArray, etc.)
- **Promises** (`Promises/`), **Modules** (`Modules/`), **Intl** (`Intl/`), **Regex** (`Regex/`, with generated unicode property-escape tables), **Source** (`Source/`: `SourceText`), **Host** (`Host/`: `IHostHooks`, host-object table for DOM bridging), **Diagnostics**, **Logging**

> Build constraints: `net10.0`, `Nullable=enable`, **`TreatWarningsAsErrors=true`**, `AllowUnsafeBlocks=false`. New code must be warning-clean and fully null-annotated.

### FenBrowser.Core
Platform-agnostic primitives. Key subsystems:
- **DOM V2** (`Dom/V2/`): `Node`, `Element`, `Document`, `ContainerNode`, `Text`, `ShadowRoot`, `EventTarget`, `MutationObserver`, `Range`, `TreeWalker`, `SelectorEngine`
- **CSS** (`Css/`): `CssComputed`, `ICssEngine`, `StyleCache`
- **Parsing** (`Parsing/`): `HtmlTokenizer`, `HtmlTreeBuilder`, `HtmlParser`, `StreamingHtmlParser`, `PreloadScanner`, `ParserSecurityPolicy`
- **Network** (`Network/`): `NetworkClient`, `MimeSniffer`, `WhatwgUrl` (full WHATWG URL state machine), handler pipeline (CSP, CORS, HSTS, AdBlock, SafeBrowsing, TrackingPrevention)
- **Security** (`Security/`): `CspPolicy`, `CorbFilter`, `OopifPlanner`, sandbox policies
- **Accessibility** (`Accessibility/`): `AccessibilityTree`, `AccessibilityRole`, `AccNameCalculator`, `AccDescCalculator`, platform bridges (UIA/AT-SPI2/NSAccessibility)
- **Memory** (`Memory/`): `ArenaAllocator` (unsafe bump-pointer), `EngineMetrics`, `TimelineTracer`, `FrameBudgetMonitor`
- **Storage** (`Storage/`): `StoragePartitioning`, partitioned cookie/KV/HTTP cache stores
- **WebIDL** (`WebIDL/`): `WebIdlParser`, `WebIdlBindingGenerator` (IDL → C# stubs)
- **Engine lifecycle** (`Engine/`): `EnginePhase`, `PhaseGuard`, `PipelineContext`, `NavigationLifecycle`, `DirtyFlags`
- **Logging**: `FenLogger.Warn/Info/Debug(msg, LogCategory.X)` — needs `using FenBrowser.Core; using FenBrowser.Core.Logging;`

### FenBrowser.FenEngine
The rendering and scripting engine. Key subsystems:
- **JavaScript Engine (LEGACY — not the active engine)** (`Core/`): `FenRuntime` → `BytecodeCompiler` (`Core/Bytecode/Compiler/`) → `VirtualMachine` (`Core/Bytecode/VM/`). Integrated via `JavaScriptEngine` (`Scripting/JavaScriptEngine.cs`) which implements `IDomBridge`. **For JS work use `FenBrowser.Js` instead.**
- **JS Types** (`Core/Types/`): `JsMap`, `JsSet`, `JsWeakMap`, `JsWeakSet`, `JsPromise`, `JsBigInt`, `JsSymbol`, `JsTypedArray`, `JsIntl`
- **DOM Wrappers** (`DOM/`): `ElementWrapper`, `DomWrapperFactory`, `CustomElementRegistry`, `MutationObserver`
- **Layout Engine** (`Layout/`): `LayoutEngine`, `BoxTreeBuilder`, formatting contexts (Block, Inline, Flex, Grid, Table, Float, AbsolutePosition), `MarginCollapseComputer`, `TextLayoutComputer`
- **Rendering** (`Rendering/`): SkiaSharp-based paint pipeline, `BrowserApi` (WebDriver API facade), compositing (`DamageTracker`, `BaseFrameReusePolicy`, `FrameBudgetAdaptivePolicy`), `BidiResolver`
- **CSS Engine** (`Rendering/Css/`): `CascadeEngine`, custom properties, container queries, media range queries
- **Web APIs** (`WebAPIs/`): Fetch, XHR, WebStorage, IndexedDB, Cache/CacheStorage, WebAudio, WebRTC, IntersectionObserver, ResizeObserver
- **Workers** (`Workers/`): `WorkerRuntime`
- **WebIDL Bindings** (`Bindings/Generated/`): auto-generated before each build from `FenBrowser.Core/WebIDL/Idl/*.idl`
- **JIT** (`Jit/`): `JitRuntime`, `BytecodeCompiler`, `FenBytecode`
- **Event Loop** (`Core/EventLoop/`): `EventLoopCoordinator`, `MicrotaskQueue`, `TaskQueue`

### FenBrowser.Host
Process orchestration and platform integration:
- `BrowserIntegration` — connects `BrowserHost` to the render loop; manages double-buffered display list, event queue, engine thread
- `ChromeManager` — Chrome/Chromium process management
- **Process Isolation** (`ProcessIsolation/`): `BrokeredProcessIsolationCoordinator`, `NetworkProcessIpc`, `GpuProcessIpc`, `UtilityProcessIpc`, `IpcFuzzHarness`; shared memory frame delivery from renderer children
- **WebDriver** (`WebDriver/`): `FenBrowserDriver`, `HostBrowserDriver`

### FenBrowser.DevTools
Chrome DevTools Protocol (CDP)-compatible debug server:
- `DevToolsServer`, `RemoteDebugServer`, `IDevToolsHost`
- Domains: `DomDomain`, `CSSDomain`, `RuntimeDomain`

### FenBrowser.WebDriver
W3C WebDriver protocol implementation:
- `CommandHandler`, `WindowCommands`, `OriginValidator`

## Key Patterns

- **`InternalsVisibleTo`**: `FenBrowser.Js` exposes internals to `FenBrowser.Js.Tests`, `FenBrowser.Js.Test262` (and the other `FenBrowser.Js.*` tools); `FenBrowser.Core`/`FenBrowser.FenEngine` expose internals to `FenBrowser.Tests`, `FenBrowser.Conformance`
- **Unsafe code**: `FenBrowser.Core` and `FenBrowser.FenEngine` enable `AllowUnsafeBlocks` (arena allocator, rendering); `FenBrowser.Js` does **not** (`AllowUnsafeBlocks=false`)
- **Nullable**: Core and FenEngine use `<Nullable>disable</Nullable>`; `FenBrowser.Js`, `FenBrowser.Js.Tests`, and `FenBrowser.Tests` use `enable`
- **SkiaSharp**: rendering backend for both Core and FenEngine; `HarfBuzzSharp` for text shaping in FenEngine
- **`BuildInParallel>false`**: set in Tests project to avoid MSBuild project-reference instability

## Namespace → Path Quick Reference

Knowing the namespace means you can go directly to the file without searching:

| Namespace | Path |
|-----------|------|
| `FenBrowser.Js.Lexer` | `FenBrowser.Js/Lexer/` |
| `FenBrowser.Js.Parser` / `FenBrowser.Js.Ast` | `FenBrowser.Js/Parser/`, `FenBrowser.Js/Ast/` |
| `FenBrowser.Js.Bytecode` | `FenBrowser.Js/Bytecode/` |
| `FenBrowser.Js.Interpreter` | `FenBrowser.Js/Interpreter/` |
| `FenBrowser.Js.Runtime` | `FenBrowser.Js/Runtime/` |
| `FenBrowser.Js.Objects` / `FenBrowser.Js.Heap` | `FenBrowser.Js/Objects/`, `FenBrowser.Js/Heap/` |
| `FenBrowser.Js.Builtins` | `FenBrowser.Js/Builtins/` |
| `FenBrowser.Js.Source` | `FenBrowser.Js/Source/` |
| `FenBrowser.Js.Host` | `FenBrowser.Js/Host/` |
| `FenBrowser.Js.Tests` | `FenBrowser.Js.Tests/` |
| `FenBrowser.Core.Dom.V2` | `FenBrowser.Core/Dom/V2/` |
| `FenBrowser.Core.Parsing` | `FenBrowser.Core/Parsing/` |
| `FenBrowser.Core.Network` | `FenBrowser.Core/Network/` |
| `FenBrowser.Core.Network.Handlers` | `FenBrowser.Core/Network/Handlers/` |
| `FenBrowser.Core.Security` | `FenBrowser.Core/Security/` |
| `FenBrowser.Core.Accessibility` | `FenBrowser.Core/Accessibility/` |
| `FenBrowser.Core.Logging` | `FenBrowser.Core/Logging/` |
| `FenBrowser.Core.Engine` | `FenBrowser.Core/Engine/` |
| `FenBrowser.Core.Storage` | `FenBrowser.Core/Storage/` |
| `FenBrowser.Core.Memory` | `FenBrowser.Core/Memory/` |
| `FenBrowser.Core.WebIDL` | `FenBrowser.Core/WebIDL/` |
| `FenBrowser.FenEngine.Core` | `FenBrowser.FenEngine/Core/` |
| `FenBrowser.FenEngine.Core.Types` | `FenBrowser.FenEngine/Core/Types/` |
| `FenBrowser.FenEngine.Core.Bytecode` | `FenBrowser.FenEngine/Core/Bytecode/` |
| `FenBrowser.FenEngine.Core.EventLoop` | `FenBrowser.FenEngine/Core/EventLoop/` |
| `FenBrowser.FenEngine.Scripting` | `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs` |
| `FenBrowser.FenEngine.DOM` | `FenBrowser.FenEngine/DOM/` |
| `FenBrowser.FenEngine.WebAPIs` | `FenBrowser.FenEngine/WebAPIs/` |
| `FenBrowser.FenEngine.Layout` | `FenBrowser.FenEngine/Layout/` |
| `FenBrowser.FenEngine.Rendering` | `FenBrowser.FenEngine/Rendering/` |
| `FenBrowser.FenEngine.Rendering.Css` | `FenBrowser.FenEngine/Rendering/Css/` |
| `FenBrowser.FenEngine.Workers` | `FenBrowser.FenEngine/Workers/` |
| `FenBrowser.FenEngine.Jit` | `FenBrowser.FenEngine/Jit/` |
| `FenBrowser.Host` | `FenBrowser.Host/` |
| `FenBrowser.Host.ProcessIsolation` | `FenBrowser.Host/ProcessIsolation/` |
| `FenBrowser.DevTools` | `FenBrowser.DevTools/Core/` |
| `FenBrowser.Tests.Engine` | `FenBrowser.Tests/Engine/` |
| `FenBrowser.Tests.DOM` | `FenBrowser.Tests/DOM/` |
| `FenBrowser.Tests.Layout` | `FenBrowser.Tests/Layout/` |
| `FenBrowser.Tests.Rendering` | `FenBrowser.Tests/Rendering/` |
| `FenBrowser.Tests.WebAPIs` | `FenBrowser.Tests/WebAPIs/` |

## Where to Add New Things

| Task | File(s) to edit |
|------|----------------|
| New JS built-in (Array/Object/etc. method) | `FenBrowser.Js/Builtins/<Name>.cs` + `FenBrowser.Js.Tests/<Name>Tests.cs` |
| New JS opcode | `FenBrowser.Js/Bytecode/` (opcode + `BytecodeCompiler` + `BytecodeVerifier`) + `FenBrowser.Js/Interpreter/BytecodeInterpreter*.cs` |
| New JS syntax | `FenBrowser.Js/Lexer/` + `FenBrowser.Js/Parser/` + `FenBrowser.Js/Ast/` (+ AstValidation) then compiler/interpreter |
| New JS engine unit test | `FenBrowser.Js.Tests/<FeatureName>Tests.cs` |
| New Web API (legacy engine) | `FenBrowser.FenEngine/WebAPIs/<ApiName>.cs` + register in `Scripting/JavaScriptEngine.cs` |
| New JS built-in type | `FenBrowser.FenEngine/Core/Types/Js<TypeName>.cs` |
| New JS built-in op/opcode | `FenBrowser.FenEngine/Core/Bytecode/OpCode.cs` + `Compiler/BytecodeCompiler.cs` + `VM/VirtualMachine.cs` |
| New DOM method/property | `FenBrowser.Core/Dom/V2/Element.cs` or `Document.cs` or `Node.cs` |
| New CSS property | `FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs` + `FenBrowser.Core/Css/CssComputed.cs` |
| New layout feature | `FenBrowser.FenEngine/Layout/LayoutEngine.cs` + relevant formatting context |
| New network handler | `FenBrowser.Core/Network/Handlers/<HandlerName>.cs` + wire up in `NetworkClient.cs` |
| New DevTools domain | `FenBrowser.DevTools/Domains/<Name>Domain.cs` + register in `DevToolsServer.cs` |
| New IDL interface | `FenBrowser.Core/WebIDL/Idl/<Name>.idl` (rebuild FenEngine to regenerate bindings) |
| New security policy | `FenBrowser.Core/Security/` |
| New process IPC channel | `FenBrowser.Host/ProcessIsolation/` |
| New unit test | `FenBrowser.Tests/<Category>/<FeatureName>Tests.cs` |

## JS Engine API Quick Reference (`FenBrowser.Js`)

The active engine has a three-stage pipeline. This is exactly how the tests drive it:

```csharp
using FenBrowser.Js.Bytecode;     // BytecodeCompiler, BytecodeVerifier
using FenBrowser.Js.Interpreter;  // BytecodeInterpreter, JsThrownException
using FenBrowser.Js.Source;       // SourceText

// Compile → verify → execute
var fn = new BytecodeCompiler().CompileScript(new SourceText("var x = 1 + 2; x;"));
new BytecodeVerifier().Verify(fn);                 // optional but standard in tests
JsValue result = new BytecodeInterpreter().Execute(fn);

// JsValue (FenBrowser.Js.Runtime) — the universal value type
result.AsBoolean();   // → bool
result.AsNumber();    // → double
// thrown JS errors surface as a CLR exception:
Assert.Throws<JsThrownException>(() => new BytecodeInterpreter().Execute(badFn));
```

Typical test helper:
```csharp
private static bool RunBool(string src)
{
    var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
    new BytecodeVerifier().Verify(fn);
    return new BytecodeInterpreter().Execute(fn).AsBoolean();
}
```

Lower-level allocation/GC is via `JsIsolate`/`JsHeap`/`HandleScope` (`FenBrowser.Js.Runtime` / `FenBrowser.Js.Heap`); host/DOM integration goes through `IHostHooks` + the host-object table in `FenBrowser.Js/Host/`.

> The legacy `FenRuntime`/`FenValue`/`FenObject`/`FenFunction` API in `FenBrowser.FenEngine` is **not** the active engine — don't use it for new JS work.

## Logging

```csharp
using FenBrowser.Core;
using FenBrowser.Core.Logging;

FenLogger.Info("message", LogCategory.JavaScript);
FenLogger.Warn("message", LogCategory.Network);
FenLogger.Debug("message", LogCategory.DOM);
```

Available `LogCategory` values: `Navigation`, `Rendering`, `CSS`, `JavaScript`, `Network`, `Images`, `Layout`, `Events`, `Storage`, `Performance`, `Errors`, `DOM`, `General`, `HtmlParsing`, `CssParsing`, `JsExecution`, `FeatureGaps`, `ServiceWorker`, `WebDriver`, `Cascade`, `ComputedStyle`, `Text`, `Paint`, `Frame`, `Verification`

## Test Patterns

- **JS engine tests** (`FenBrowser.Js.Tests`, namespace `FenBrowser.Js.Tests`) compile+verify+execute source directly (see JS Engine API Quick Reference) — no DOM, no network, no base class. One test file per feature: `ArrayIsArrayTests.cs` → class `ArrayIsArrayTests`.
- Browser-engine tests that need DOM use helpers in `FenBrowser.Tests/Layout/LayoutTestHelper.cs`.
- Test class names match the file; namespace matches folder.
- All tests are `[Fact]` or `[Theory]` (xUnit); no base class required.
