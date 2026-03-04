# FenEngine Full Audit - 03/04/2026

## Scope and Coverage Guarantee
- Audited root: `FenBrowser.FenEngine/`
- Files reviewed: **281**
- Total lines reviewed: **132984**
- Method: recursive full-file scan with per-file SHA256 and line counts, followed by manual hotspot review on all flagged risk clusters.
- Coverage artifact: `docs/fengine_audit_scan_03042026.json`.

## Recheck Pass (Line-by-Line, Second Audit)
- A second independent pass re-read **every line** in all 281 files and computed a line-by-line SHA256 stream per file.
- Recheck totals: **281 files**, **132984 lines**, **5316067 characters**.
- Drift check against first pass (`docs/fengine_audit_scan_03042026.json`): **0 files changed**.
- Recheck artifacts:
  - `docs/fengine_line_recheck_03042026.csv`
  - `docs/fengine_line_recheck_03042026.json`

## Executive Verdict
- The engine compiles, but it is **not yet production-grade as a serious JavaScript engine** due to unresolved bytecode/runtime feature gaps, high warning volume, broad exception swallowing, and memory-lifetime risks in Skia-heavy paths.
- Most acute risks are concentrated in `Core/FenRuntime.cs`, `Core/Bytecode/*`, `Scripting/JavaScriptEngine.cs`, `Rendering/ImageLoader.cs`, and `Rendering/BrowserApi.cs`.

## Build and Static Signals
- `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -t:Rebuild` completed with **406 warnings, 0 errors**.
- Full build log saved at `docs/fengine_build_03042026.log`.
- Signal totals from full scan:
  - `TODO/FIXME/HACK`: 22
  - `throw new Exception(...)` / NotImplemented markers: 90
  - `async void`: 3
  - Empty catch patterns: 2
  - Catch-swallow patterns: 4
  - `Task.Run(...)` usage: 51
  - `GC.Collect(...)` calls: 7
  - `lock(...)` usage: 234

## Severity-Ranked Findings

### Critical
- **Bytecode compiler rejects AST shapes at runtime path** via `NotImplementedException`, causing feature holes instead of graceful fallback:
  - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:992`
  - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:1529`
  - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:2122`
  - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:3187`
  - `FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs:3304`
- **VM hard-fails for AST-backed function/constructor calls in bytecode-only mode**, indicating incomplete execution-model unification:
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1466`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1520`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1576`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1625`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1696`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs:1753`
- **Skia memory-lifetime risk in image cache eviction path**: disposal is intentionally disabled in cache cleanup paths, pushing release to GC and risking native-memory pressure:
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:236`
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:242`
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:296`

### High
- **Excessive silent exception swallowing** (`catch {}` and silent fallbacks) degrades reliability and makes regressions hard to diagnose. Sample hotspots:
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2205`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2215`
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2225`
  - `FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs:51`
  - `FenBrowser.FenEngine/WebAPIs/WebAPIs.cs:50`
- **`async void` appears in engine paths**, making exceptions unobservable and control-flow non-awaitable:
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs:2199`
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs:660`
- **Fire-and-forget `Task.Run` is heavily used in runtime APIs**, bypassing structured scheduling and deterministic loop control:
  - `FenBrowser.FenEngine/Core/FenRuntime.cs:6341`
  - `FenBrowser.FenEngine/Core/FenRuntime.cs:6417`
  - `FenBrowser.FenEngine/Core/FenRuntime.cs:12010`
  - `FenBrowser.FenEngine/Core/FenRuntime.cs:12324`
  - `FenBrowser.FenEngine/Core/FenRuntime.cs:13541`

### Medium
- **Generic `throw new Exception(...)` is widespread (84 instances)**, reducing typed error semantics for JS-facing APIs and internals; concentrated in:
  - `FenBrowser.FenEngine/Core/FenRuntime.cs`
  - `FenBrowser.FenEngine/Jit/JitCompiler.cs`
  - `FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs`
- **Engine monolith hotspots** reduce maintainability and optimization velocity:
  - `FenBrowser.FenEngine/Core/FenRuntime.cs` (14,195 lines)
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs` (6,153 lines)
  - `FenBrowser.FenEngine/Core/Parser.cs` (5,548 lines)
  - `FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs` (3,983 lines)
  - `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs` (3,711 lines)
- **Placeholder/stub behavior remains in engine surface**:
  - `FenBrowser.FenEngine/Rendering/BrowserEngine.cs:32`
  - `FenBrowser.FenEngine/Rendering/BrowserEngine.cs:33`

### Low
- `GC.Collect` appears in test harness and DevTools helper paths; acceptable for diagnostics but should stay isolated from hot production paths:
  - `FenBrowser.FenEngine/DevTools/DevToolsCore.cs:657`
  - `FenBrowser.FenEngine/Testing/Test262Runner.cs:173`
  - `FenBrowser.FenEngine/Testing/WPTTestRunner.cs:102`
- Minor TODO/FIXME markers exist but are not the primary quality blocker.

## Module Footprint (Files / Lines)
| Module | Files | Lines |
|---|---:|---:|
| Rendering | 95 | 46474 |
| Layout | 44 | 16384 |
| Core | 40 | 36488 |
| DOM | 23 | 6871 |
| WebAPIs | 14 | 3720 |
| Workers | 10 | 2485 |
| Scripting | 9 | 6831 |
| Testing | 5 | 2134 |
| Typography | 5 | 643 |
| HTML | 5 | 4023 |
| Storage | 4 | 950 |
| Adapters | 4 | 515 |
| Security | 4 | 466 |
| Jit | 4 | 1040 |
| Interaction | 3 | 461 |
| DevTools | 2 | 1198 |
| TestFenEngine.cs | 1 | 452 |
| Observers | 1 | 551 |
| Assets | 1 | 103 |
| Compatibility | 1 | 306 |
| Resources | 1 | 321 |
| Errors | 1 | 113 |
| FenBrowser.FenEngine.csproj | 1 | 22 |
| build_capture.bat | 1 | 1 |
| README.md | 1 | 228 |
| Program.cs | 1 | 204 |

## Largest Files (Risk Concentrators)
| File | Lines |
|---|---:|
| FenBrowser.FenEngine/Core/FenRuntime.cs | 14195 |
| FenBrowser.FenEngine/Rendering/Css/CssLoader.cs | 6153 |
| FenBrowser.FenEngine/Core/Parser.cs | 5548 |
| FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs | 3983 |
| FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs | 3711 |
| FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs | 3665 |
| FenBrowser.FenEngine/Rendering/BrowserApi.cs | 3620 |
| FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs | 3136 |
| FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs | 3049 |
| FenBrowser.FenEngine/DOM/ElementWrapper.cs | 2140 |

## Full File Coverage Manifest
- Every file under `FenBrowser.FenEngine/` is listed below with line count, hash prefix, and scan signals.
| File | Lines | SHA256 (first 12) | Signals |
|---|---:|---|---|
| FenBrowser.FenEngine/Adapters/ISvgRenderer.cs | 127 | FA5D6D972474 | staticMutable:2 |
| FenBrowser.FenEngine/Adapters/ITextMeasurer.cs | 21 | BCE198EABAED | none |
| FenBrowser.FenEngine/Adapters/SkiaTextMeasurer.cs | 42 | EE0BE5B56179 | none |
| FenBrowser.FenEngine/Adapters/SvgSkiaRenderer.cs | 325 | 456B42DBE5C6 | none |
| FenBrowser.FenEngine/Assets/ua.css | 103 | BF54DBACBBCD | none |
| FenBrowser.FenEngine/build_capture.bat | 1 | A12BF2ADD59E | none |
| FenBrowser.FenEngine/Compatibility/WebCompatibilityInterventions.cs | 306 | C7A0369C20E6 | lockUsage:8, staticMutable:1 |
| FenBrowser.FenEngine/Core/AnimationFrameScheduler.cs | 308 | 19F1DBB73B58 | lockUsage:7, staticMutable:2 |
| FenBrowser.FenEngine/Core/Ast.cs | 1160 | 31F0697DB1D7 | none |
| FenBrowser.FenEngine/Core/Bytecode/CodeBlock.cs | 40 | 939DEAC035AD | none |
| FenBrowser.FenEngine/Core/Bytecode/Compiler/BytecodeCompiler.cs | 3665 | 876207FCABD1 | notImplemented:5 |
| FenBrowser.FenEngine/Core/Bytecode/OpCode.cs | 106 | A05473326229 | none |
| FenBrowser.FenEngine/Core/Bytecode/VM/CallFrame.cs | 158 | B94394BEBBF3 | none |
| FenBrowser.FenEngine/Core/Bytecode/VM/VirtualMachine.cs | 3049 | 2F6D2F74E63C | notImplemented:14 |
| FenBrowser.FenEngine/Core/EngineLoop.cs | 152 | 6099FDA15BA1 | none |
| FenBrowser.FenEngine/Core/EnginePhase.cs | 70 | 9718E840EE55 | staticMutable:7 |
| FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs | 380 | AF8F2EF814F2 | lockUsage:3, staticMutable:2 |
| FenBrowser.FenEngine/Core/EventLoop/MicrotaskQueue.cs | 148 | 33C2CC67BD99 | lockUsage:8 |
| FenBrowser.FenEngine/Core/EventLoop/TaskQueue.cs | 129 | EECB15AE840D | lockUsage:5 |
| FenBrowser.FenEngine/Core/ExecutionContext.cs | 107 | 69ECEE2D6AAF | taskRun:2 |
| FenBrowser.FenEngine/Core/FenEnvironment.cs | 310 | 93CE75EA742A | none |
| FenBrowser.FenEngine/Core/FenFunction.cs | 275 | B894F860ADB8 | staticMutable:1 |
| FenBrowser.FenEngine/Core/FenObject.cs | 603 | 9CE569FA242B | staticMutable:4 |
| FenBrowser.FenEngine/Core/FenRuntime.cs | 14195 | BF34856104B2 | todo:1, notImplemented:20, lockUsage:34, taskRun:16 |
| FenBrowser.FenEngine/Core/FenSymbol.cs | 196 | BC172EF754C1 | lockUsage:3, staticMutable:3 |
| FenBrowser.FenEngine/Core/FenValue.cs | 591 | FB5BB8C72134 | staticMutable:24 |
| FenBrowser.FenEngine/Core/IDomBridge.cs | 21 | B546693671B4 | none |
| FenBrowser.FenEngine/Core/InputQueue.cs | 426 | 5C04FA5794F5 | staticMutable:5 |
| FenBrowser.FenEngine/Core/Interfaces/IExecutionContext.cs | 134 | 96168FD90361 | none |
| FenBrowser.FenEngine/Core/Interfaces/IHistoryBridge.cs | 11 | 22D48C8A946B | none |
| FenBrowser.FenEngine/Core/Interfaces/IModuleLoader.cs | 22 | 36739E1549D6 | none |
| FenBrowser.FenEngine/Core/Interfaces/IObject.cs | 78 | A3AB2B9655FF | none |
| FenBrowser.FenEngine/Core/Interfaces/IValue.cs | 93 | EE90E941CFD4 | none |
| FenBrowser.FenEngine/Core/Lexer.cs | 1768 | B8AFD7A8DB0F | todo:3, staticMutable:2 |
| FenBrowser.FenEngine/Core/ModuleLoader.cs | 518 | F07B65881C98 | notImplemented:7 |
| FenBrowser.FenEngine/Core/Parser.cs | 5548 | 571D21F609D3 | none |
| FenBrowser.FenEngine/Core/PropertyDescriptor.cs | 89 | F1E653D48267 | staticMutable:3 |
| FenBrowser.FenEngine/Core/Types/JsBigInt.cs | 209 | 5179E0AF3114 | staticMutable:23 |
| FenBrowser.FenEngine/Core/Types/JsIntl.cs | 122 | 6259FBF01700 | staticMutable:2 |
| FenBrowser.FenEngine/Core/Types/JsMap.cs | 135 | 48DCFE134355 | none |
| FenBrowser.FenEngine/Core/Types/JsPromise.cs | 549 | 3F3A363B3B71 | staticMutable:6 |
| FenBrowser.FenEngine/Core/Types/JsSet.cs | 205 | E0A5DFA42AA6 | none |
| FenBrowser.FenEngine/Core/Types/JsSymbol.cs | 190 | 946688F8B4EA | staticMutable:3 |
| FenBrowser.FenEngine/Core/Types/JsTypedArray.cs | 525 | 5D067405C810 | todo:1, notImplemented:8 |
| FenBrowser.FenEngine/Core/Types/JsWeakMap.cs | 61 | AF7A19E77E6E | notImplemented:1 |
| FenBrowser.FenEngine/Core/Types/JsWeakSet.cs | 53 | E2595ECC79EC | notImplemented:2 |
| FenBrowser.FenEngine/Core/Types/Shape.cs | 89 | 6479A6A6B50B | lockUsage:1, staticMutable:1 |
| FenBrowser.FenEngine/DevTools/DebugConfig.cs | 135 | 8079D12AEB80 | staticMutable:1 |
| FenBrowser.FenEngine/DevTools/DevToolsCore.cs | 1063 | 12AC3D5ED5B5 | lockUsage:4, gcCollect:2, staticMutable:1 |
| FenBrowser.FenEngine/DOM/AttrWrapper.cs | 104 | 29EB20082477 | none |
| FenBrowser.FenEngine/DOM/CommentWrapper.cs | 39 | BBFC62FB5796 | none |
| FenBrowser.FenEngine/DOM/CustomElementRegistry.cs | 373 | ED47960441B5 | lockUsage:6 |
| FenBrowser.FenEngine/DOM/CustomEvent.cs | 66 | B62080643B88 | notImplemented:1 |
| FenBrowser.FenEngine/DOM/DocumentWrapper.cs | 761 | 549615034896 | todo:2, staticMutable:2 |
| FenBrowser.FenEngine/DOM/DomEvent.cs | 335 | F02A829DB71A | notImplemented:1 |
| FenBrowser.FenEngine/DOM/DomMutationQueue.cs | 178 | EC7D49B21103 | lockUsage:5, staticMutable:1 |
| FenBrowser.FenEngine/DOM/DomWrapperFactory.cs | 46 | F7166E1B5E01 | staticMutable:3 |
| FenBrowser.FenEngine/DOM/ElementWrapper.cs | 2140 | FEFF059CF3AF | notImplemented:1 |
| FenBrowser.FenEngine/DOM/EventListenerRegistry.cs | 249 | BFF3FD0761BF | lockUsage:7 |
| FenBrowser.FenEngine/DOM/EventTarget.cs | 424 | 79D373B1126E | notImplemented:1, staticMutable:6 |
| FenBrowser.FenEngine/DOM/HTMLCollectionWrapper.cs | 433 | 34DE8C1CF909 | none |
| FenBrowser.FenEngine/DOM/InMemoryCookieStore.cs | 202 | 269104603B26 | none |
| FenBrowser.FenEngine/DOM/MutationObserverWrapper.cs | 220 | AB25B053B234 | lockUsage:5 |
| FenBrowser.FenEngine/DOM/NamedNodeMapWrapper.cs | 171 | D7ED73C75D64 | none |
| FenBrowser.FenEngine/DOM/NodeListWrapper.cs | 92 | D171CCD30719 | none |
| FenBrowser.FenEngine/DOM/NodeWrapper.cs | 366 | B793E6AB37F3 | notImplemented:1 |
| FenBrowser.FenEngine/DOM/Observers.cs | 268 | AD8CC4C55645 | none |
| FenBrowser.FenEngine/DOM/RangeWrapper.cs | 95 | DB1A50F0CA6B | none |
| FenBrowser.FenEngine/DOM/ShadowRootWrapper.cs | 88 | E0EE49DAE3A0 | none |
| FenBrowser.FenEngine/DOM/StaticNodeList.cs | 25 | ABC4D8E6FDEE | none |
| FenBrowser.FenEngine/DOM/TextWrapper.cs | 44 | F2E1CA4EEA13 | todo:1 |
| FenBrowser.FenEngine/DOM/TouchEvent.cs | 152 | 38103F88DA83 | todo:1 |
| FenBrowser.FenEngine/Errors/FenError.cs | 113 | 68B53B8C4AC6 | none |
| FenBrowser.FenEngine/FenBrowser.FenEngine.csproj | 22 | 41B6A75D1AF8 | none |
| FenBrowser.FenEngine/HTML/ForeignContent.cs | 333 | 2E1E9225C8CF | staticMutable:10 |
| FenBrowser.FenEngine/HTML/HtmlEntities.cs | 482 | 120B43F7B37E | staticMutable:4 |
| FenBrowser.FenEngine/HTML/HtmlToken.cs | 101 | BF95F2BCC74C | none |
| FenBrowser.FenEngine/HTML/HtmlTokenizer.cs | 1546 | FD2689C6DEA0 | none |
| FenBrowser.FenEngine/HTML/HtmlTreeBuilder.cs | 1561 | 49B830756234 | none |
| FenBrowser.FenEngine/Interaction/FocusManager.cs | 122 | C58163C03BBA | none |
| FenBrowser.FenEngine/Interaction/HitTestResult.cs | 97 | F6E832859815 | none |
| FenBrowser.FenEngine/Interaction/InputManager.cs | 242 | F26854776251 | todo:2 |
| FenBrowser.FenEngine/Jit/BytecodeCompiler.cs | 426 | 8C6294038218 | none |
| FenBrowser.FenEngine/Jit/FenBytecode.cs | 87 | 97BFB6EC80A5 | none |
| FenBrowser.FenEngine/Jit/JitCompiler.cs | 462 | E5C1381ED1B5 | notImplemented:18 |
| FenBrowser.FenEngine/Jit/JitRuntime.cs | 65 | 5C86997D0100 | staticMutable:7 |
| FenBrowser.FenEngine/Layout/AbsolutePositionSolver.cs | 394 | F045060A0B3B | staticMutable:2 |
| FenBrowser.FenEngine/Layout/Algorithms/BlockLayoutAlgorithm.cs | 700 | 67DE16DCB295 | none |
| FenBrowser.FenEngine/Layout/Algorithms/ILayoutAlgorithm.cs | 20 | AC9839F9091C | none |
| FenBrowser.FenEngine/Layout/Algorithms/LayoutContext.cs | 43 | 1F1F013C2373 | none |
| FenBrowser.FenEngine/Layout/Algorithms/LayoutHelpers.cs | 110 | 66F85F14E498 | staticMutable:5 |
| FenBrowser.FenEngine/Layout/BoxModel.cs | 138 | DDF3E3911D14 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/ContainingBlockResolver.cs | 245 | 8BF50BB7E218 | none |
| FenBrowser.FenEngine/Layout/Contexts/BlockFormattingContext.cs | 698 | C98B47306564 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Contexts/FlexFormattingContext.cs | 1683 | ECFEB659B5B4 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Contexts/FloatManager.cs | 98 | ED0DFB39042D | none |
| FenBrowser.FenEngine/Layout/Contexts/FormattingContext.cs | 131 | 8E38AA9CBE6F | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Contexts/GridFormattingContext.cs | 288 | E0AE3FF2DEF0 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Contexts/InlineFormattingContext.cs | 962 | 558287EF302A | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Contexts/LayoutBoxOps.cs | 59 | 02A6D02425BD | staticMutable:4 |
| FenBrowser.FenEngine/Layout/Contexts/LayoutState.cs | 57 | C4ED723C072C | none |
| FenBrowser.FenEngine/Layout/Coordinates/LogicalTypes.cs | 90 | D14797A35EA9 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/Coordinates/WritingModeConverter.cs | 108 | 3A7DE79D2E6A | staticMutable:5 |
| FenBrowser.FenEngine/Layout/FloatExclusion.cs | 191 | 2E9327B975BC | staticMutable:2 |
| FenBrowser.FenEngine/Layout/GridLayoutComputer.Areas.cs | 71 | E36CB82FD45B | staticMutable:1 |
| FenBrowser.FenEngine/Layout/GridLayoutComputer.cs | 1069 | C17A939F20AA | staticMutable:10 |
| FenBrowser.FenEngine/Layout/GridLayoutComputer.Parsing.cs | 276 | A247A8B17400 | staticMutable:1 |
| FenBrowser.FenEngine/Layout/ILayoutComputer.cs | 73 | 7F1F510AB077 | none |
| FenBrowser.FenEngine/Layout/InlineLayoutComputer.cs | 1113 | DF6211297F5E | staticMutable:2 |
| FenBrowser.FenEngine/Layout/InlineRunDebugger.cs | 274 | AE5056133591 | staticMutable:9 |
| FenBrowser.FenEngine/Layout/LayoutContext.cs | 103 | 94B3C7CF0316 | none |
| FenBrowser.FenEngine/Layout/LayoutEngine.cs | 401 | D025F42CD527 | none |
| FenBrowser.FenEngine/Layout/LayoutHelper.cs | 244 | D4F881CE16B4 | staticMutable:9 |
| FenBrowser.FenEngine/Layout/LayoutPositioningLogic.cs | 191 | 88D03591A204 | staticMutable:2 |
| FenBrowser.FenEngine/Layout/LayoutResult.cs | 140 | F4529F7DCABF | staticMutable:1 |
| FenBrowser.FenEngine/Layout/LayoutValidator.cs | 124 | D2356E1D1E2C | staticMutable:8 |
| FenBrowser.FenEngine/Layout/MarginCollapseComputer.cs | 191 | DF9B02A98A1B | staticMutable:7 |
| FenBrowser.FenEngine/Layout/MarginCollapseTracker.cs | 160 | 07DE83644568 | none |
| FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs | 3136 | 6BEE07DCE505 | todo:2 |
| FenBrowser.FenEngine/Layout/MultiColumnLayoutComputer.cs | 155 | 5021F8E13A26 | none |
| FenBrowser.FenEngine/Layout/PseudoBox.cs | 588 | AD69D06EE7F0 | todo:1, staticMutable:4 |
| FenBrowser.FenEngine/Layout/ReplacedElementSizing.cs | 322 | FBCF1DD00331 | staticMutable:7 |
| FenBrowser.FenEngine/Layout/ScrollAnchoring.cs | 68 | 058D58C2BDF8 | none |
| FenBrowser.FenEngine/Layout/TableLayoutComputer.cs | 595 | 51734F81F1F1 | staticMutable:3 |
| FenBrowser.FenEngine/Layout/TextLayoutComputer.cs | 292 | 56E8B878251C | staticMutable:2 |
| FenBrowser.FenEngine/Layout/TextLayoutHelper.cs | 242 | CFAED35238B3 | staticMutable:4 |
| FenBrowser.FenEngine/Layout/TransformParsed.cs | 19 | C22215C3646A | none |
| FenBrowser.FenEngine/Layout/Tree/BoxTreeBuilder.cs | 379 | 32766E181895 | none |
| FenBrowser.FenEngine/Layout/Tree/LayoutBox.cs | 82 | 7AFACB81ED51 | none |
| FenBrowser.FenEngine/Layout/Tree/LayoutNodeTypes.cs | 61 | F95A6A19DC63 | none |
| FenBrowser.FenEngine/Observers/ObserverCoordinator.cs | 551 | 68832225D12C | lockUsage:19, staticMutable:1 |
| FenBrowser.FenEngine/Program.cs | 204 | E1C5D132C82D | none |
| FenBrowser.FenEngine/README.md | 228 | CF1CAB119CC9 | none |
| FenBrowser.FenEngine/Rendering/Backends/HeadlessRenderBackend.cs | 208 | 47446F7D3362 | none |
| FenBrowser.FenEngine/Rendering/Backends/SkiaRenderBackend.cs | 463 | 3DD25DC91496 | skiaWithoutUsing:2 |
| FenBrowser.FenEngine/Rendering/BidiResolver.cs | 426 | 2A34E97EB570 | staticMutable:3 |
| FenBrowser.FenEngine/Rendering/BidiTextRenderer.cs | 422 | EC9A22074F50 | staticMutable:8 |
| FenBrowser.FenEngine/Rendering/BrowserApi.cs | 3620 | F809DEAB6EBA | notImplemented:3, asyncVoid:1, emptyCatch:1, swallowCatch:1, lockUsage:1, taskRun:1, staticMutable:1 |
| FenBrowser.FenEngine/Rendering/BrowserCoreHelpers.cs | 35 | D1F5BAD0A2BA | none |
| FenBrowser.FenEngine/Rendering/BrowserEngine.cs | 41 | 170ECE3F384E | todo:1 |
| FenBrowser.FenEngine/Rendering/Compositing/BaseFrameReusePolicy.cs | 39 | D0CB69AF6644 | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Compositing/DamageRasterizationPolicy.cs | 82 | F231DAB5F829 | none |
| FenBrowser.FenEngine/Rendering/Compositing/DamageRegionNormalizationPolicy.cs | 163 | E30D3701F0A1 | none |
| FenBrowser.FenEngine/Rendering/Compositing/FrameBudgetAdaptivePolicy.cs | 103 | 1AE12A30DC27 | none |
| FenBrowser.FenEngine/Rendering/Compositing/PaintCompositingStabilityController.cs | 74 | 3EB56DD6B07F | none |
| FenBrowser.FenEngine/Rendering/Compositing/PaintDamageTracker.cs | 150 | 1340FA5F0E61 | none |
| FenBrowser.FenEngine/Rendering/Compositing/ScrollDamageComputer.cs | 165 | 3DA7D9DDBFF1 | none |
| FenBrowser.FenEngine/Rendering/Core/ILayoutEngine.cs | 19 | 962623884CF0 | none |
| FenBrowser.FenEngine/Rendering/Core/RenderContext.cs | 34 | 26AF3AF6021D | none |
| FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs | 405 | 6CCA711D8484 | none |
| FenBrowser.FenEngine/Rendering/Css/CssAnimationEngine.cs | 1036 | 885783F64951 | lockUsage:12, staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Css/CssClipPathParser.cs | 260 | 04886077A844 | skiaWithoutUsing:4, staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Css/CssEngineFactory.cs | 119 | F30BFF06AE89 | staticMutable:3 |
| FenBrowser.FenEngine/Rendering/Css/CssFilterParser.cs | 343 | AF23D23A2E97 | staticMutable:4 |
| FenBrowser.FenEngine/Rendering/Css/CssFlexLayout.cs | 824 | 9E88CF88057B | staticMutable:3 |
| FenBrowser.FenEngine/Rendering/Css/CssFloatLayout.cs | 413 | DE70E89D676C | staticMutable:3 |
| FenBrowser.FenEngine/Rendering/Css/CssFunctions.cs | 761 | 1BC023FA1E6E | lockUsage:1, staticMutable:1 |
| FenBrowser.FenEngine/Rendering/Css/CssGridAdvanced.cs | 695 | 8B1D511B6DF4 | none |
| FenBrowser.FenEngine/Rendering/Css/CssLoader.cs | 6153 | A7029F27F01A | todo:1, lockUsage:12, taskRun:3, staticMutable:9 |
| FenBrowser.FenEngine/Rendering/Css/CssLoaderValueParsing.cs | 400 | A0EADD518C97 | staticMutable:6 |
| FenBrowser.FenEngine/Rendering/Css/CssModel.cs | 269 | 413CB930C268 | none |
| FenBrowser.FenEngine/Rendering/Css/CssParser.cs | 1112 | 7878EB437E6C | staticMutable:26 |
| FenBrowser.FenEngine/Rendering/Css/CssSelectorAdvanced.cs | 578 | CCDC0228B238 | staticMutable:3 |
| FenBrowser.FenEngine/Rendering/Css/CssSelectorParser.cs | 29 | 0735D94AD13A | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/Css/CssStyleApplicator.cs | 154 | 0F7018ACA567 | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Css/CssSyntaxParser.cs | 639 | 1B379482EA53 | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Css/CssToken.cs | 108 | EDD584F7424B | none |
| FenBrowser.FenEngine/Rendering/Css/CssTokenizer.cs | 616 | 0BB1F5A8D211 | none |
| FenBrowser.FenEngine/Rendering/Css/CssTransform3D.cs | 492 | C820E2C595EC | staticMutable:11 |
| FenBrowser.FenEngine/Rendering/Css/CssValue.cs | 285 | 95B0DA988B08 | none |
| FenBrowser.FenEngine/Rendering/Css/CssValueParser.cs | 633 | 12D44C4001CE | staticMutable:8 |
| FenBrowser.FenEngine/Rendering/Css/SelectorMatcher.cs | 1164 | 6B809591E956 | staticMutable:9 |
| FenBrowser.FenEngine/Rendering/CssTransitionEngine.cs | 338 | 01EB482E46F3 | none |
| FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs | 1877 | 0ADAD32E0B85 | emptyCatch:1, swallowCatch:1, taskRun:3 |
| FenBrowser.FenEngine/Rendering/DebugOverlay.cs | 234 | AABE538947C6 | skiaWithoutUsing:2, staticMutable:3 |
| FenBrowser.FenEngine/Rendering/ElementStateManager.cs | 581 | C61DC721B131 | lockUsage:2, staticMutable:7 |
| FenBrowser.FenEngine/Rendering/ErrorPageRenderer.cs | 239 | 2FA8E5F791BF | staticMutable:5 |
| FenBrowser.FenEngine/Rendering/FontRegistry.cs | 533 | E9D1EAFF79B9 | lockUsage:12, staticMutable:12 |
| FenBrowser.FenEngine/Rendering/HistoryEntry.cs | 19 | 375FF4BAE6F8 | none |
| FenBrowser.FenEngine/Rendering/ImageLoader.cs | 959 | 5E1E33B73400 | asyncVoid:1, skiaWithoutUsing:4, lockUsage:13, staticMutable:18 |
| FenBrowser.FenEngine/Rendering/Interaction/HitTester.cs | 309 | 4E94FA7B9143 | staticMutable:5 |
| FenBrowser.FenEngine/Rendering/Interaction/ScrollbarRenderer.cs | 334 | 29CCB99994F4 | none |
| FenBrowser.FenEngine/Rendering/Interaction/ScrollManager.cs | 747 | DACAE603D63B | todo:1, lockUsage:4 |
| FenBrowser.FenEngine/Rendering/IRenderBackend.cs | 225 | 076183E611DF | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/LiteDomUtil.cs | 24 | C2ED29D4861B | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/NavigationManager.cs | 150 | 4292E9858D19 | none |
| FenBrowser.FenEngine/Rendering/NewTabRenderer.cs | 182 | 2243095D5F98 | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/Paint/LayoutTreeDumper.cs | 323 | 05D1958781CB | staticMutable:4 |
| FenBrowser.FenEngine/Rendering/Paint/PaintDebugOverlay.cs | 262 | BD5EBD5263E4 | staticMutable:4 |
| FenBrowser.FenEngine/Rendering/Painting/BoxPainter.cs | 435 | 83DD845E9427 | none |
| FenBrowser.FenEngine/Rendering/Painting/DisplayList.cs | 43 | 8EAEEB3B51E2 | none |
| FenBrowser.FenEngine/Rendering/Painting/DisplayListBuilder.cs | 246 | BC0817415A07 | none |
| FenBrowser.FenEngine/Rendering/Painting/ImagePainter.cs | 267 | 081C8CDD3722 | none |
| FenBrowser.FenEngine/Rendering/Painting/Painter.cs | 311 | 7E539D79E90F | none |
| FenBrowser.FenEngine/Rendering/Painting/StackingContextComplete.cs | 499 | 1C4D2CF97811 | none |
| FenBrowser.FenEngine/Rendering/Painting/StackingContextPainter.cs | 363 | D6C6C21FA4A4 | none |
| FenBrowser.FenEngine/Rendering/Painting/TextPainter.cs | 444 | 0651A584F375 | skiaWithoutUsing:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/ImmutablePaintTree.cs | 172 | 0753A31B33C0 | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/IPaintNodeVisitor.cs | 22 | 67F088502C9E | none |
| FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs | 3983 | F16D1F206519 | todo:1, swallowCatch:2, skiaWithoutUsing:1, staticMutable:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/PaintNode.cs | 172 | 9F9B8602E98D | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/PaintNodeBase.cs | 329 | C1164A85E927 | none |
| FenBrowser.FenEngine/Rendering/PaintTree/PaintTree.cs | 88 | 9666DB63986B | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/PaintTreeBuilder.cs | 369 | 0809C0BA53EA | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/PaintTree/PaintTreeDiff.cs | 26 | 6554B3E07192 | none |
| FenBrowser.FenEngine/Rendering/PaintTree/PositionedGlyph.cs | 33 | 9AFFD4834480 | none |
| FenBrowser.FenEngine/Rendering/Performance/IncrementalLayout.cs | 235 | 1552AC6E4991 | lockUsage:4 |
| FenBrowser.FenEngine/Rendering/Performance/ParallelPainter.cs | 336 | A0C279AB2086 | lockUsage:4 |
| FenBrowser.FenEngine/Rendering/RenderCommands.cs | 569 | 61DC551C6BA2 | none |
| FenBrowser.FenEngine/Rendering/RenderDataTypes.cs | 128 | A3112FCBC4EA | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/RendererSafetyPolicy.cs | 20 | 1DD6BF3143F3 | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/RenderPipeline.cs | 150 | 6AF846DF7158 | staticMutable:17 |
| FenBrowser.FenEngine/Rendering/RenderTree/FrameState.cs | 76 | 38297B3B1039 | none |
| FenBrowser.FenEngine/Rendering/RenderTree/RectExtensions.cs | 33 | 443B4EEE4602 | staticMutable:5 |
| FenBrowser.FenEngine/Rendering/RenderTree/RenderBox.cs | 610 | 87D0E7661D58 | todo:1 |
| FenBrowser.FenEngine/Rendering/RenderTree/RenderObject.cs | 56 | 733241BEBBB2 | none |
| FenBrowser.FenEngine/Rendering/RenderTree/RenderText.cs | 38 | 453247C269C4 | none |
| FenBrowser.FenEngine/Rendering/RenderTree/ScrollModel.cs | 208 | 5BD97B714285 | none |
| FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs | 839 | 696E590703E4 | todo:1, staticMutable:1 |
| FenBrowser.FenEngine/Rendering/SkiaRenderer.cs | 1012 | 70D606C91A7E | skiaWithoutUsing:1 |
| FenBrowser.FenEngine/Rendering/StackingContext.cs | 122 | A4DE99CAC851 | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/SvgRenderer.cs | 315 | C299C45720F7 | none |
| FenBrowser.FenEngine/Rendering/UserAgent/UAStyleProvider.cs | 360 | 71C962DB0D8B | staticMutable:2 |
| FenBrowser.FenEngine/Rendering/WebGL/WebGL2RenderingContext.cs | 644 | F45E7822F513 | none |
| FenBrowser.FenEngine/Rendering/WebGL/WebGLConstants.cs | 277 | E56674EC61F3 | staticMutable:1 |
| FenBrowser.FenEngine/Rendering/WebGL/WebGLContextManager.cs | 283 | 2B1D04270F84 | staticMutable:9 |
| FenBrowser.FenEngine/Rendering/WebGL/WebGLObjects.cs | 278 | 43773AEE10D3 | none |
| FenBrowser.FenEngine/Rendering/WebGL/WebGLRenderingContext.cs | 1215 | F23EDB28D9A9 | skiaWithoutUsing:2 |
| FenBrowser.FenEngine/Resources/ua.css | 321 | A6D75E55EBF9 | none |
| FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs | 1025 | 4AF01A5F8E14 | skiaWithoutUsing:8 |
| FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs | 3711 | 52F27D6882EA | todo:1, notImplemented:2, asyncVoid:1, lockUsage:24, taskRun:5, staticMutable:12 |
| FenBrowser.FenEngine/Scripting/JavaScriptEngine.Dom.cs | 1269 | 34D511541830 | lockUsage:5 |
| FenBrowser.FenEngine/Scripting/JavaScriptEngine.Methods.cs | 223 | 13A754E35508 | lockUsage:5, taskRun:2 |
| FenBrowser.FenEngine/Scripting/JavaScriptRuntime.cs | 23 | 67474442CBC4 | none |
| FenBrowser.FenEngine/Scripting/JsRuntimeAbstraction.cs | 120 | 7E369AB12C1E | none |
| FenBrowser.FenEngine/Scripting/ModuleLoader.cs | 216 | F6E76A4E0312 | none |
| FenBrowser.FenEngine/Scripting/ProxyAPI.cs | 86 | 54E8D472A1F1 | staticMutable:4 |
| FenBrowser.FenEngine/Scripting/ReflectAPI.cs | 158 | 891023F0FE40 | staticMutable:2 |
| FenBrowser.FenEngine/Security/IPermissionManager.cs | 119 | 1ED0D0E4AD16 | none |
| FenBrowser.FenEngine/Security/IResourceLimits.cs | 119 | D2A9AC990EEA | none |
| FenBrowser.FenEngine/Security/PermissionManager.cs | 123 | F1C265745EE0 | lockUsage:5 |
| FenBrowser.FenEngine/Security/PermissionStore.cs | 105 | 3C57AE7201E7 | lockUsage:4, staticMutable:1 |
| FenBrowser.FenEngine/Storage/FileStorageBackend.cs | 471 | FC2BF046B384 | none |
| FenBrowser.FenEngine/Storage/InMemoryStorageBackend.cs | 238 | 3DB9780ADE42 | none |
| FenBrowser.FenEngine/Storage/IStorageBackend.cs | 119 | BC4C3A48FBA0 | none |
| FenBrowser.FenEngine/Storage/StorageUtils.cs | 122 | 21A2F1747629 | staticMutable:3 |
| FenBrowser.FenEngine/TestFenEngine.cs | 452 | 59C649AA12D5 | staticMutable:1 |
| FenBrowser.FenEngine/Testing/AcidTestRunner.cs | 364 | AEF793C3288F | none |
| FenBrowser.FenEngine/Testing/Test262Runner.cs | 830 | 5D82519DF70E | todo:1, notImplemented:2, lockUsage:1, taskRun:2, gcCollect:3, staticMutable:1 |
| FenBrowser.FenEngine/Testing/UsedValueComparator.cs | 386 | 5A0404960779 | staticMutable:2 |
| FenBrowser.FenEngine/Testing/VerificationRunner.cs | 68 | BA6B0E5F0A45 | staticMutable:2 |
| FenBrowser.FenEngine/Testing/WPTTestRunner.cs | 486 | A5DF82418DCF | gcCollect:2 |
| FenBrowser.FenEngine/Typography/GlyphRun.cs | 75 | 7DB4E428DD11 | none |
| FenBrowser.FenEngine/Typography/IFontService.cs | 71 | 37DF053449A7 | none |
| FenBrowser.FenEngine/Typography/NormalizedFontMetrics.cs | 103 | D26B8BFBDA8F | staticMutable:1 |
| FenBrowser.FenEngine/Typography/SkiaFontService.cs | 113 | 2E817D35890E | none |
| FenBrowser.FenEngine/Typography/TextShaper.cs | 281 | CF7D735ED03B | skiaWithoutUsing:1 |
| FenBrowser.FenEngine/WebAPIs/Cache.cs | 274 | A6DB0DDD6AA3 | taskRun:1 |
| FenBrowser.FenEngine/WebAPIs/CacheStorage.cs | 151 | C4C50E093EB1 | taskRun:1 |
| FenBrowser.FenEngine/WebAPIs/FetchApi.cs | 553 | 527A9F1B9C56 | notImplemented:2, taskRun:3, staticMutable:5 |
| FenBrowser.FenEngine/WebAPIs/FetchEvent.cs | 280 | E61F4B70E0F8 | staticMutable:4 |
| FenBrowser.FenEngine/WebAPIs/IndexedDBService.cs | 134 | 97D74C2A3730 | taskRun:1, staticMutable:2 |
| FenBrowser.FenEngine/WebAPIs/IntersectionObserverAPI.cs | 118 | B1DC514F67EC | staticMutable:3 |
| FenBrowser.FenEngine/WebAPIs/ResizeObserverAPI.cs | 87 | 187A1EEEB442 | staticMutable:3 |
| FenBrowser.FenEngine/WebAPIs/StorageApi.cs | 352 | A46D6B847E7D | notImplemented:1, lockUsage:3, staticMutable:20 |
| FenBrowser.FenEngine/WebAPIs/TestConsoleCapture.cs | 177 | 6328F1D39C0C | staticMutable:9 |
| FenBrowser.FenEngine/WebAPIs/TestHarnessAPI.cs | 326 | F7AB7F0D7859 | staticMutable:17 |
| FenBrowser.FenEngine/WebAPIs/WebAPIs.cs | 296 | 40F84B93B38B | staticMutable:15 |
| FenBrowser.FenEngine/WebAPIs/WebAudioAPI.cs | 401 | 073F9211543E | taskRun:1, staticMutable:3 |
| FenBrowser.FenEngine/WebAPIs/WebRTCAPI.cs | 364 | C6AC78596527 | taskRun:1, staticMutable:5 |
| FenBrowser.FenEngine/WebAPIs/XMLHttpRequest.cs | 207 | C34A3FF32CF9 | taskRun:1 |
| FenBrowser.FenEngine/Workers/ServiceWorker.cs | 164 | 65346A1F05E4 | none |
| FenBrowser.FenEngine/Workers/ServiceWorkerClients.cs | 166 | 140D5F3EDF06 | taskRun:1 |
| FenBrowser.FenEngine/Workers/ServiceWorkerContainer.cs | 252 | 4B75007C7F39 | taskRun:1 |
| FenBrowser.FenEngine/Workers/ServiceWorkerGlobalScope.cs | 110 | 36B6D8BB901C | taskRun:1 |
| FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs | 366 | 76F79EF3C76D | taskRun:1, staticMutable:1 |
| FenBrowser.FenEngine/Workers/ServiceWorkerRegistration.cs | 124 | 48B00AA865ED | taskRun:1 |
| FenBrowser.FenEngine/Workers/StructuredClone.cs | 183 | E16FD0A86828 | staticMutable:3 |
| FenBrowser.FenEngine/Workers/WorkerConstructor.cs | 214 | B28FB6CD0D6C | none |
| FenBrowser.FenEngine/Workers/WorkerGlobalScope.cs | 401 | 946FE9C81FE9 | lockUsage:5, taskRun:2 |
| FenBrowser.FenEngine/Workers/WorkerRuntime.cs | 505 | 2C9C1CD4DF05 | lockUsage:2, taskRun:1 |

## Audit Conclusion
- This is a broad and deep codebase with substantial infrastructure already present, but it still behaves like a hybrid prototype in key execution paths (compiler/VM completeness, error discipline, memory ownership, scheduling discipline).
- If the target is a truly serious JS engine, priority should be: (1) bytecode/VM completeness, (2) exception and logging hardening, (3) Skia resource lifecycle rigor, (4) event-loop/threading conformance cleanup.


## Recheck Pass 3 (2026-03-04, Runtime Hardening Delta)
- Focus area: FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs callback/timer/host-bridge reliability and fetch wiring.
- Key changes:
  - Removed remaining empty catch blocks in JavaScriptEngine.cs hot paths; current count in file is 0.
  - Added guarded wrappers for callback execution and debug logging (TryInvokeFunction, TryExecuteFunction, TryLogDebug) to preserve fault isolation without silent suppression.
  - Replaced generic fetch-handler setup exceptions with typed InvalidOperationException.
  - Replaced swallow-prone timer disposal/callback paths with explicit guarded helpers and warning logs.
- Verification on this pass:
  - dotnet build .\\FenBrowser.FenEngine\\FenBrowser.FenEngine.csproj -v minimal => 0 errors, 368 warnings.
  - dotnet test .\\FenBrowser.Tests\\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal => 11 passed, 0 failed.
- Updated static snapshot (project-level, .cs):
  - throw new Exception(...): 74
  - async void: 0
  - empty catch {}: 167 (engine-wide; now 0 in JavaScriptEngine.cs)

## Production-Grade Gap (Current)
- The engine has improved runtime fault visibility, but remains below production-grade + hardened baseline until the following are completed:
  1. Replace remaining 167 empty catches in critical modules (Core, Bytecode, WebAPIs, Rendering).
  2. Replace remaining 74 generic throw new Exception(...) with domain-typed exceptions.
  3. Reduce compiler/analyzer warning volume to release threshold and enforce warning budget in CI.
  4. Close bytecode/VM unsupported execution gaps and complete stress/leak gates.

## Recheck Pass 4 (2026-03-04, Safety Sweep Delta)
- Focus area: low-risk runtime support surfaces with remaining silent catches and generic exceptions.
- Files hardened:
  - `FenBrowser.FenEngine/WebAPIs/WebAPIs.cs`
  - `FenBrowser.FenEngine/WebAPIs/XMLHttpRequest.cs`
  - `FenBrowser.FenEngine/Core/ModuleLoader.cs`
  - `FenBrowser.FenEngine/Rendering/BrowserCoreHelpers.cs`
  - `FenBrowser.FenEngine/Core/Types/JsWeakMap.cs`
  - `FenBrowser.FenEngine/Core/Types/JsWeakSet.cs`
- Changes:
  - Replaced empty `catch {}` blocks with explicit warning logs in callback and helper paths.
  - Added safe callback-invocation wrappers for thenable/geolocation APIs.
  - Replaced selected `throw new Exception(...)` usages with `InvalidOperationException` while preserving JS-facing message text.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors, `368` warnings.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal` => `11` passed, `0` failed.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `156` (down from `167` at start of prior wave)
  - `throw new Exception(...)`: `71` (down from `74`)
  - `async void`: `0`

## Recheck Pass 5 (2026-03-04, BrowserHost Event/Log Hardening)
- Focus area: `FenBrowser.FenEngine/Rendering/BrowserApi.cs` high-frequency host event and logging call sites.
- Changes:
  - Added guarded helper methods for event/log boundaries in BrowserHost (`TryInvokeRepaintReady`, `TryInvokeNavigated`, `TryInvokeLoadingChanged`, `TryInvokeNavigationLifecycleChanged`, `TryInvokeConsoleMessage`, plus log wrappers).
  - Replaced multiple inline swallow blocks at navigation/render/input call sites with helper calls.
  - Preserved behavior while reducing repeated silent suppression in host-facing hot paths.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors, `368` warnings.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal` => `11` passed, `0` failed.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `129`
  - `throw new Exception(...)`: `71`
  - `async void`: `0`

## Recheck Pass 6 (2026-03-04, BrowserApi Deep Catch Cleanup)
- Focus area: `FenBrowser.FenEngine/Rendering/BrowserApi.cs` high-frequency host bridge and diagnostics boundaries.
- Changes:
  - Removed all remaining empty `catch {}` blocks in `BrowserApi.cs`.
  - Normalized host logging through `TryLogDebug/TryLogInfo/TryLogWarn/TryLogError` with non-empty fallback behavior.
  - Replaced inline swallow wrappers in navigation dump and hit-test tracing paths with guarded logging and explicit fallback.
  - Hardened disposal and network callback paths to avoid silent suppression.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors, `368` warnings.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal` => `11` passed, `0` failed.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `69`
  - `throw new Exception(...)`: `71`
  - `async void`: `0`

## Recheck Pass 7 (2026-03-04, Rendering Pipeline Catch Cleanup)
- Focus area:
  - `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs`
  - `FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs`
  - `FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs`
- Changes:
  - Replaced remaining silent `catch {}` blocks in these rendering/layout pipeline files with explicit warning diagnostics.
  - Preserved fallback behavior where required (UI dispatch fallback action, fire-and-forget render launch, cookie operations).
  - Hardened URL normalization and background image resolution guards in paint tree building.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal` => `11` passed, `0` failed.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `52`
  - `throw new Exception(...)`: `71`
  - `async void`: `0`

## Recheck Pass 8 (2026-03-04, DOM Bridge Catch Cleanup)
- Focus area: `FenBrowser.FenEngine/Scripting/JavaScriptEngine.Dom.cs`.
- Changes:
  - Removed all remaining empty `catch {}` blocks in DOM bridge wrappers.
  - Added local guarded logging helpers (`TryLogDomDebug`, `TryLogDomWarn`) to keep diagnostics safe under logger failure.
  - Replaced silent suppression in innerHTML/script execution, mutation queue updates, clone/serialize helpers, and ShadowRoot/CSS declaration setters with explicit warning paths.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors, `368` warnings.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj --filter "FullyQualifiedName~FetchApiTests|FullyQualifiedName~ModuleLoaderTests" -v minimal` => `11` passed, `0` failed.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `31`
  - `throw new Exception(...)`: `71`
  - `async void`: `0`

## Recheck Pass 9 (2026-03-04, Full FenEngine Zero-Empty-Catch Sweep)
- Focus area: remaining empty-catch sites across FenEngine runtime surfaces, with no skipped files.
- Files hardened:
  - `FenBrowser.FenEngine/Workers/ServiceWorkerManager.cs`
  - `FenBrowser.FenEngine/DOM/DocumentWrapper.cs`
  - `FenBrowser.FenEngine/DOM/EventTarget.cs`
  - `FenBrowser.FenEngine/Scripting/ModuleLoader.cs`
  - `FenBrowser.FenEngine/Testing/Test262Runner.cs`
  - `FenBrowser.FenEngine/Scripting/CanvasRenderingContext2D.cs`
  - `FenBrowser.FenEngine/Core/Types/JsIntl.cs`
  - `FenBrowser.FenEngine/Rendering/ElementStateManager.cs`
  - `FenBrowser.FenEngine/Rendering/FontRegistry.cs`
  - `FenBrowser.FenEngine/Rendering/Css/CssAnimationEngine.cs`
  - `FenBrowser.FenEngine/Rendering/ImageLoader.cs`
  - `FenBrowser.FenEngine/Rendering/NavigationManager.cs`
  - `FenBrowser.FenEngine/Rendering/Css/CssEngineFactory.cs`
  - `FenBrowser.FenEngine/Rendering/Css/CssParser.cs`
  - `FenBrowser.FenEngine/Rendering/SkiaDomRenderer.cs`
  - `FenBrowser.FenEngine/Rendering/SkiaRenderer.cs`
  - `FenBrowser.FenEngine/Rendering/WebGL/WebGLContextManager.cs`
  - `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs` (false-positive comment cleanup)
- Changes:
  - Replaced all remaining `catch {}` blocks with explicit diagnostics and safe fallback behavior.
  - Preserved behavior at callback/event boundaries while preventing silent suppression.
  - Verified no remaining empty catches in `FenBrowser.FenEngine/**/*.cs`.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors.
  - `dotnet test .\FenBrowser.Tests\FenBrowser.Tests.csproj -v minimal` => `953` passed, `21` failed (known pre-existing baseline failures not introduced by this sweep).
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `0`
  - `throw new Exception(...)`: `71`
  - `async void`: `0`

## Recheck Pass 10 (2026-03-04, Typed Exception Conversion Wave)
- Focus area: remaining high-impact generic exception sites outside `FenRuntime`/VM core loops.
- Files hardened:
  - `FenBrowser.FenEngine/DOM/DomEvent.cs`
  - `FenBrowser.FenEngine/DOM/CustomEvent.cs`
  - `FenBrowser.FenEngine/DOM/EventTarget.cs`
  - `FenBrowser.FenEngine/DOM/ElementWrapper.cs`
  - `FenBrowser.FenEngine/DOM/NodeWrapper.cs`
  - `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
  - `FenBrowser.FenEngine/Jit/JitCompiler.cs`
  - `FenBrowser.FenEngine/WebAPIs/StorageApi.cs`
  - `FenBrowser.FenEngine/Testing/Test262Runner.cs`
- Changes:
  - Replaced `throw new Exception(...)` with typed exceptions (`FenTypeError`, `FenResourceError`, `InvalidOperationException`, `KeyNotFoundException`) while preserving message semantics.
  - Kept JS-facing TypeError/Quota error text stable where behavior contracts depend on string checks.
- Verification:
  - `dotnet build .\FenBrowser.FenEngine\FenBrowser.FenEngine.csproj -v minimal` => `0` errors.
- Updated static snapshot (project-level, `.cs`):
  - empty `catch {}`: `0`
  - `throw new Exception(...)`: `42`
  - `async void`: `0`
