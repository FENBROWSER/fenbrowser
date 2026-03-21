# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

## Build Commands

```bash
# Build entire solution
dotnet build FenBrowser.sln -c Release

# Build individual projects
dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release
dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Release
dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Release
dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj -c Release

# FenEngine build auto-runs WebIDL binding generation before compile
# (WebIdlGen reads FenBrowser.Core/WebIDL/Idl/*.idl → writes FenBrowser.FenEngine/Bindings/Generated/)
```

## Running Tests

```bash
# Run all unit tests (xUnit)
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj

# Run a specific test class or method
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~JavaScriptEngineModuleLoadingTests"
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"

# Run WPT (Web Platform Tests)
dotnet build FenBrowser.WPT/FenBrowser.WPT.csproj -c Release
./FenBrowser.WPT/bin/Release/net8.0/FenBrowser.WPT.exe run_category <name>
# e.g. run_category accname, run_category accessibility, run_category dom
# Results written to wpt_results.md
```

## Test262 Conformance Testing Protocol

Test262 tests MUST be run in **chunks of 1000** with strict safety controls:

1. **Chunk Size**: 1000 tests per chunk (53 total chunks)
2. **Completion Threshold**: Each chunk MUST have **900+ tests complete** (pass or fail) before moving to the next chunk.
3. **Memory Safety**: Check RAM before each chunk. **NEVER exceed 70% RAM usage**.
4. **Results File**: All results go to `docs/test_results.md`.
5. **Per-test timeout**: 180 seconds.

```bash
# Build once
dotnet build FenBrowser.Test262/FenBrowser.Test262.csproj -c Release

# CLI usage
./FenBrowser.Test262/bin/Release/net8.0/FenBrowser.Test262.exe get_chunk_count
./FenBrowser.Test262/bin/Release/net8.0/FenBrowser.Test262.exe run_chunk <N>
./FenBrowser.Test262/bin/Release/net8.0/FenBrowser.Test262.exe run_category <name> [--max <N>]
./FenBrowser.Test262/bin/Release/net8.0/FenBrowser.Test262.exe run_single <path>

# Check RAM (Windows) — FreePhysicalMemory must be > 10000000 KB (~10 GB) before each chunk
powershell -Command "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory"
```

### Chunk Validation Rules
- Record: chunk number, range, time, passed, failed, pass%, avg/test
- If chunk crashes or <900 tests complete, retry once before moving on

### Results Format
Appended as markdown table rows to `docs/test_results.md`:
```
| Chunk | Range | Time (ms) | Tests | Passed | Failed | Pass % | Avg/Test (ms) |
```

## Architecture Overview

FenBrowser is a multi-process browser engine written in C#/.NET 8, structured around six main layers:

### Project Dependency Graph
```
FenBrowser.Core          ← foundational types, DOM, CSS interfaces, networking
    ↑
FenBrowser.FenEngine     ← JS engine, layout, rendering, WebAPIs (depends on Core)
    ↑
FenBrowser.Host          ← process orchestration, BrowserIntegration, IPC coordinators
FenBrowser.DevTools      ← CDP-like debug server (DOM/CSS/Runtime domains)
FenBrowser.WebDriver     ← WebDriver protocol (W3C)
FenBrowser.Tests         ← xUnit tests (references Core, FenEngine, DevTools, Host)
FenBrowser.Test262       ← ECMAScript Test262 CLI runner
FenBrowser.WPT           ← Web Platform Tests runner
FenBrowser.WebIdlGen     ← WebIDL parser → C# binding code generator
```

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
- **JavaScript Engine** (`Core/`): `FenRuntime` → `BytecodeCompiler` (`Core/Bytecode/Compiler/`) → `VirtualMachine` (`Core/Bytecode/VM/`). Integrated via `JavaScriptEngine` (`Scripting/JavaScriptEngine.cs`) which implements `IDomBridge`.
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

- **`InternalsVisibleTo`**: `FenBrowser.Core` and `FenBrowser.FenEngine` expose internals to `FenBrowser.Tests`, `FenBrowser.Test262`, `FenBrowser.WPT`, `FenBrowser.Conformance`
- **Unsafe code**: `FenBrowser.Core` and `FenBrowser.FenEngine` both enable `AllowUnsafeBlocks` (arena allocator, rendering)
- **Nullable**: Core and FenEngine use `<Nullable>disable</Nullable>`; Tests uses `enable`
- **SkiaSharp**: rendering backend for both Core and FenEngine; `HarfBuzzSharp` for text shaping in FenEngine
- **`BuildInParallel>false`**: set in Tests project to avoid MSBuild project-reference instability

## Namespace → Path Quick Reference

Knowing the namespace means you can go directly to the file without searching:

| Namespace | Path |
|-----------|------|
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
| New Web API | `FenBrowser.FenEngine/WebAPIs/<ApiName>.cs` + register in `Scripting/JavaScriptEngine.cs` |
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

## JS Engine API Quick Reference

Tests and engine code interact with FenEngine through these core types:

```csharp
// Standalone runtime (used directly in tests — no DOM needed)
var runtime = new FenRuntime();
runtime.ExecuteSimple("var x = 1 + 2;");
FenValue result = runtime.GetGlobal("x");   // → FenValue{Number, 3.0}

// FenValue — struct representing any JS value
FenValue.Undefined / FenValue.Null
FenValue.FromString("hello")
FenValue.FromNumber(42.0)
FenValue.FromBoolean(true)
FenValue.FromObject(fenObj)
FenValue.FromFunction(fenFn)
result.Type          // Interfaces.ValueType enum: Undefined/Null/Boolean/Number/String/Object/Function
result.ToNumber()    // → double
result.ToString2()   // → string  (avoid .ToString() — use .ToString2() or cast)
result.IsFunction    // bool
result.AsFunction()  // → FenFunction

// FenObject — JS object
var obj = new FenObject();
obj.Set("key", FenValue.FromString("val"));
obj.Get("key");      // → FenValue

// FenFunction — callable
var fn = new FenFunction("myFn", (args, thisVal) => FenValue.FromNumber(args[0].ToNumber() * 2));
```

For full DOM+WebAPI integration (navigation, script execution in a page context), use `JavaScriptEngine` in `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs`.

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

- Tests instantiate `new FenRuntime()` directly for pure JS engine tests (no DOM or network).
- Tests that need DOM use helpers in `FenBrowser.Tests/Layout/LayoutTestHelper.cs`.
- Test class names match the file: `FenBrowser.Tests/Engine/FooTests.cs` → class `FooTests`, namespace `FenBrowser.Tests.Engine`.
- All tests are `[Fact]` or `[Theory]` (xUnit); no base class required.
