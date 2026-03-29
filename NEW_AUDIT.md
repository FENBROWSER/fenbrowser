# NEW_AUDIT

## Scope
- Audit performed directly from source, not from existing docs.
- Included projects: `FenBrowser.Core`, `FenBrowser.FenEngine`, `FenBrowser.Host`, `FenBrowser.DevTools`, `FenBrowser.WebDriver`, `FenBrowser.WebIdlGen`.
- Excluded: `docs/**`, `FenBrowser.Conformance`, `FenBrowser.Core.Tests`, `FenBrowser.Tests`, `FenBrowser.Test262`, `FenBrowser.WPT`, `bin/**`, `obj/**`, and existing markdown docs.
- Ratings used: `stub`, `basic`, `partial`, `production-grade`.

## Completeness Recheck
- Rechecked the live filesystem inventory after generating this audit.
- In-scope files found: `586`.
- Matching per-file audit rows in this document: `586`.
- Missing in-scope files from the audit: `0`.
- Duplicate per-file audit rows: `0`.
- The only non-file table rows are the `6` project-summary rows.
- Intentionally still in scope because they live inside main projects: `FenBrowser.FenEngine\Testing\*`, `FenBrowser.FenEngine\WebAPIs\TestHarnessAPI.cs`, `FenBrowser.FenEngine\Program.cs`, and `FenBrowser.Host\Program.cs`.

## Overall Verdict
The browser is implementation-heavy, especially in `FenEngine` and `Core`. The strongest areas are JavaScript runtime/parser depth, HTML parsing in `Core`, CSS loading/cascade, rendering/layout, networking/resource management, and host/browser integration. The biggest weaknesses are architectural purity rather than total absence of code: duplicated parser ownership between `Core` and `FenEngine`, embedded test harness flows inside main projects, generated binding bulk that inflates apparent surface completeness, and a few clearly unfinished seams in process isolation and accessibility portability.

### Highest-Signal Findings
1. `FenBrowser.Host\ProcessIsolation\Gpu\GpuProcessIpc.cs` and `FenBrowser.Host\ProcessIsolation\Utility\UtilityProcessIpc.cs` are empty, so those IPC seams are architectural placeholders, not shipped process-channel implementations.
2. `FenBrowser.WebDriver\Class1.cs` is dead scaffold and should not exist in a serious automation layer.
3. Accessibility is mixed: `FenBrowser.Core\Accessibility\PlatformA11yBridge.cs` is large and ambitious, but Linux AT-SPI still runs in explicit stub mode, so cross-platform accessibility is partial, not production-grade.
4. Rendering architecture still carries transitional debt: `FenBrowser.FenEngine\Rendering\SkiaDomRenderer.cs` explicitly exists as a compatibility adapter instead of the final clean renderer surface.
5. HTML parsing ownership is duplicated across `FenBrowser.Core\Parsing\*` and `FenBrowser.FenEngine\HTML\*`, which weakens single-source architectural clarity.
6. Main projects still embed testing/tooling concerns. `FenBrowser.Host\Program.cs`, `FenBrowser.FenEngine\Program.cs`, `FenBrowser.FenEngine\Testing\*`, and `FenBrowser.FenEngine\WebAPIs\TestHarnessAPI.cs` all live inside runtime projects instead of being cleanly isolated.
7. Generated WebIDL bindings are broad and useful, but they should be rated `partial` by default because the files mainly expose generated surface area; true completeness depends on the bound host/runtime objects.

## Project Summary
| Project | Files | Lines | Stub | Basic | Partial | Production-grade |
|---|---:|---:|---:|---:|---:|---:|
| FenBrowser.Core | 153 | 42331 | 0 | 38 | 67 | 48 |
| FenBrowser.FenEngine | 327 | 177723 | 0 | 45 | 170 | 112 |
| FenBrowser.Host | 59 | 17628 | 2 | 8 | 32 | 17 |
| FenBrowser.DevTools | 27 | 8916 | 0 | 2 | 17 | 8 |
| FenBrowser.WebDriver | 18 | 3425 | 1 | 1 | 11 | 5 |
| FenBrowser.WebIdlGen | 2 | 133 | 0 | 1 | 1 | 0 |

## FenBrowser.Core
Core is one of the stronger layers: DOM, parsing, networking, cache, and security infrastructure are real and dense. The main caution is uneven cross-platform accessibility and a broad set of responsibilities concentrated in a few very large files.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.Core\Accessibility\AccDescCalculator.cs | 93 | partial | Accessibility support layer with useful behavior, but completeness is mixed across platforms or responsibilities. |
| FenBrowser.Core\Accessibility\AccessibilityNode.cs | 66 | basic | Accessibility support layer with useful behavior, but completeness is mixed across platforms or responsibilities. |
| FenBrowser.Core\Accessibility\AccessibilityRole.cs | 511 | production-grade | Accessibility logic with concrete AX/tree work and meaningful browser integration. |
| FenBrowser.Core\Accessibility\AccessibilityTree.cs | 156 | partial | Accessibility support layer with useful behavior, but completeness is mixed across platforms or responsibilities. |
| FenBrowser.Core\Accessibility\AccessibilityTreeBuilder.cs | 279 | production-grade | Accessibility logic with concrete AX/tree work and meaningful browser integration. |
| FenBrowser.Core\Accessibility\AccNameCalculator.cs | 337 | production-grade | Accessibility logic with concrete AX/tree work and meaningful browser integration. |
| FenBrowser.Core\Accessibility\AriaSpec.cs | 402 | production-grade | Accessibility logic with concrete AX/tree work and meaningful browser integration. |
| FenBrowser.Core\Accessibility\PlatformA11yBridge.cs | 689 | partial | Large cross-platform AX bridge; Windows path is concrete, but Linux AT-SPI still logs stub-mode behavior and TODOs, so platform ambition is ahead of shipped completeness. |
| FenBrowser.Core\Accessibility\PlatformAccessibilitySnapshot.cs | 174 | partial | Accessibility support layer with useful behavior, but completeness is mixed across platforms or responsibilities. |
| FenBrowser.Core\BrowserSettings.cs | 219 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Cache\CacheKey.cs | 48 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Cache\ShardedCache.cs | 89 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\CacheManager.cs | 402 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\CertificateInfo.cs | 51 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Compat\HttpCache.cs | 311 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\ConsoleLogger.cs | 28 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Css\CssComputed.cs | 610 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.Core\Css\CssCornerRadius.cs | 52 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.Core\Css\ICssEngine.cs | 75 | basic | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.Core\Css\StyleCache.cs | 260 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.Core\Dom\V2\Attr.cs | 142 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\CharacterData.cs | 308 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Comment.cs | 53 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\ContainerNode.cs | 953 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Document.cs | 689 | production-grade | Core document model is thoughtfully separated from Element and carries real DOM semantics rather than a legacy shortcut. |
| FenBrowser.Core\Dom\V2\DocumentFragment.cs | 84 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\DocumentType.cs | 141 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\DomException.cs | 150 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\DomExtensions.cs | 68 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\DOMTokenList.cs | 301 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Element.cs | 952 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\EventTarget.cs | 666 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Mixins.cs | 48 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\MutationObserver.cs | 507 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\NamedNodeMap.cs | 344 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Node.cs | 597 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\NodeFlags.cs | 142 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\NodeIterator.cs | 227 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\NodeList.cs | 429 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\PseudoElement.cs | 41 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\Range.cs | 806 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Security\AttributeSanitizer.cs | 387 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\Selectors\CompiledSelector.cs | 325 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Selectors\SelectorEngine.cs | 226 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Selectors\SelectorParser.cs | 514 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\Selectors\SimpleSelector.cs | 814 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.Core\Dom\V2\ShadowRoot.cs | 185 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\Text.cs | 140 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\TreeScope.cs | 119 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\Dom\V2\TreeWalker.cs | 306 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.Core\DomSerializer.cs | 217 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Engine\DirtyFlags.cs | 263 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Engine\EngineContext.cs | 201 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Engine\EngineInvariants.cs | 156 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Engine\EnginePhase.cs | 38 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Engine\NavigationLifecycle.cs | 320 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Engine\NavigationSubresourceTracker.cs | 119 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Engine\PhaseGuard.cs | 332 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Engine\PipelineContext.cs | 518 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Engine\PipelineStage.cs | 152 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\FenBrowser.Core.csproj | 30 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.Core\FenLogger.cs | 175 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\IBrowserEngine.cs | 26 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\ILogger.cs | 13 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\INetworkService.cs | 23 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Interop\Windows\Kernel32Interop.cs | 369 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Interop\Windows\ProcessThreadsInterop.cs | 212 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Interop\Windows\UserenvInterop.cs | 120 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Logging\CssSpecDefaults.cs | 136 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\DebugConfig.cs | 42 | basic | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\DebugHelper.cs | 28 | basic | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\DiagnosticPaths.cs | 86 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\EngineCapabilities.cs | 272 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\LogCategory.cs | 53 | basic | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\LogConfig.cs | 303 | production-grade | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\LogEntry.cs | 185 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\LogManager.cs | 393 | production-grade | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\LogShippingService.cs | 270 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\PerformanceProfiler.cs | 247 | production-grade | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\SpecComplianceLogger.cs | 163 | partial | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Logging\StructuredLogger.cs | 351 | production-grade | Logging/diagnostic infrastructure; small by nature, but necessary support code. |
| FenBrowser.Core\Math\CornerRadius.cs | 41 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Math\Thickness.cs | 72 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Memory\ArenaAllocator.cs | 336 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Memory\EngineMetrics.cs | 381 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\Network\EncodingSniffer.cs | 188 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\AdBlockHandler.cs | 47 | basic | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\CorsHandler.cs | 311 | production-grade | Networking/policy implementation with real transport, parsing, or security behavior. |
| FenBrowser.Core\Network\Handlers\HstsHandler.cs | 113 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\HttpHandler.cs | 122 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\PrivacyHandler.cs | 78 | basic | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\SafeBrowsingHandler.cs | 55 | basic | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Handlers\TrackingPreventionHandler.cs | 254 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\HttpClientFactory.cs | 267 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\Interfaces.cs | 50 | basic | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\MimeSniffer.cs | 184 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\NetworkClient.cs | 308 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\ResourcePrefetcher.cs | 508 | production-grade | Networking/policy implementation with real transport, parsing, or security behavior. |
| FenBrowser.Core\Network\SecureDnsResolver.cs | 206 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Core\Network\WhatwgUrl.cs | 1376 | production-grade | Networking/policy implementation with real transport, parsing, or security behavior. |
| FenBrowser.Core\NetworkConfiguration.cs | 247 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\NetworkService.cs | 76 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Parsing\HtmlParser.cs | 64 | partial | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Parsing\HtmlToken.cs | 100 | partial | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Parsing\HtmlTokenizer.cs | 1954 | production-grade | Substantial WHATWG-style tokenizer with explicit states, character references, and emission caps; one of the stronger parser implementations in the solution. |
| FenBrowser.Core\Parsing\HtmlTreeBuilder.cs | 2561 | production-grade | Heavy HTML tree-construction engine with insertion modes, checkpoints, and pipeline integration; mature implementation depth. |
| FenBrowser.Core\Parsing\ICssParser.cs | 16 | basic | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Parsing\IHtmlParser.cs | 13 | basic | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Parsing\ParserSecurityPolicy.cs | 17 | basic | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Parsing\PreloadScanner.cs | 148 | partial | Parsing support/helper file; useful, but not the main heavy parser implementation. |
| FenBrowser.Core\Platform\CrossPlatformSharedMemoryRegion.cs | 167 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Platform\IPlatformLayer.cs | 125 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Platform\ISharedMemoryRegion.cs | 79 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Platform\OSPlatformKind.cs | 20 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\Platform\PlatformLayerFactory.cs | 165 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Platform\PosixPlatformLayer.cs | 96 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Platform\Windows\WindowsPlatformLayer.cs | 164 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Platform\Windows\WindowsSharedMemoryRegion.cs | 198 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\ProcessIsolation\RendererIsolationPolicies.cs | 557 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\ResourceManager.cs | 1733 | production-grade | Central fetch/cache/policy surface with CSP, CORB, privacy, safe-browsing, and cache wiring; broad runtime responsibility and real edge-case handling. |
| FenBrowser.Core\SandboxPolicy.cs | 182 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Security\Corb\CorbFilter.cs | 352 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.Core\Security\CspPolicy.cs | 238 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Oopif\OopifPlanner.cs | 366 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.Core\Security\Origin.cs | 101 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\IOsSandboxFactory.cs | 41 | basic | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\ISandbox.cs | 127 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\NullSandbox.cs | 131 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\OsSandboxCapabilities.cs | 111 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\OsSandboxProfile.cs | 188 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\Posix\PosixCommandSandbox.cs | 536 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.Core\Security\Sandbox\Posix\PosixOsSandboxFactory.cs | 51 | basic | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\Windows\WindowsAppContainerSandbox.cs | 449 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.Core\Security\Sandbox\Windows\WindowsJobObjectSandbox.cs | 379 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\Sandbox\Windows\WindowsOsSandboxFactory.cs | 68 | basic | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.Core\Security\SecurityChecks.cs | 205 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.Core\Storage\StoragePartitioning.cs | 422 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\StreamingHtmlParser.cs | 591 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Core\System\FrameDeadline.cs | 55 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.Core\UiThreadHelper.cs | 64 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\Verification\ContentVerifier.cs | 252 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\WebIDL\Idl\Attr.idl | 27 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\CharacterData.idl | 91 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\Document.idl | 127 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\Element.idl | 58 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\Event.idl | 54 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\EventTarget.idl | 24 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\HTMLElement.idl | 61 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\Node.idl | 65 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\NodeList.idl | 16 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\Idl\Window.idl | 145 | basic | Canonical WebIDL declaration file; useful interface breadth, but runtime behavior depends on the generator and bound host objects. |
| FenBrowser.Core\WebIDL\WebIdlBindingGenerator.cs | 1110 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Core\WebIDL\WebIdlParser.cs | 918 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |

## FenBrowser.FenEngine
FenEngine is the dominant implementation layer and contains the real browser engine: JS runtime, parser, rendering, layout, workers, and Web APIs. It is powerful, but it also carries architectural duplication, generated-surface bulk, and embedded testing/harness code inside the product project.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.FenEngine\Adapters\ISvgRenderer.cs | 128 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Adapters\ITextMeasurer.cs | 22 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.FenEngine\Adapters\SkiaTextMeasurer.cs | 43 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.FenEngine\Adapters\SvgSkiaRenderer.cs | 383 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Assets\ua.css | 104 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Bindings\Generated\AddEventListenerOptionsBinding.g.cs | 42 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\AttrBinding.g.cs | 315 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\CDATASectionBinding.g.cs | 196 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\CharacterDataBinding.g.cs | 334 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\CommentBinding.g.cs | 201 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\CustomEventBinding.g.cs | 237 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\CustomEventInitBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\DocumentBinding.g.cs | 700 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\DocumentFragmentBinding.g.cs | 201 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\DocumentTypeBinding.g.cs | 241 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ElementBinding.g.cs | 827 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ElementCreationOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\EventBinding.g.cs | 509 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\EventInitBinding.g.cs | 42 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\EventListenerCallbackBinding.g.cs | 20 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\EventListenerOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\EventTargetBinding.g.cs | 258 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\FocusOptionsBinding.g.cs | 38 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\GetHTMLOptionsBinding.g.cs | 38 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\GetRootNodeOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\HTMLCollectionBinding.g.cs | 245 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\HTMLElementBinding.g.cs | 2849 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\NamedNodeMapBinding.g.cs | 332 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\NodeBinding.g.cs | 690 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\NodeListBinding.g.cs | 228 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ProcessingInstructionBinding.g.cs | 211 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ScrollBehaviorBinding.g.cs | 40 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ScrollOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ScrollToOptionsBinding.g.cs | 38 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ShadowRootBinding.g.cs | 349 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ShadowRootInitBinding.g.cs | 50 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\ShadowRootModeBinding.g.cs | 37 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\SlotAssignmentModeBinding.g.cs | 37 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\StructuredSerializeOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\TextBinding.g.cs | 233 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\WindowBinding.g.cs | 3558 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\Bindings\Generated\WindowPostMessageOptionsBinding.g.cs | 34 | partial | Auto-generated WebIDL binding with brand checks and property/method shims; breadth is good, but semantics depend on underlying host object implementations. |
| FenBrowser.FenEngine\build_capture.bat | 2 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.FenEngine\Compatibility\HostApiSurfaceCatalog.cs | 115 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Compatibility\WebCompatibilityInterventions.cs | 307 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Core\AnimationFrameScheduler.cs | 309 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Ast.cs | 1182 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Bytecode\CodeBlock.cs | 48 | basic | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Bytecode\Compiler\BytecodeCompiler.cs | 5424 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Bytecode\OpCode.cs | 124 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Bytecode\VM\CallFrame.cs | 159 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Bytecode\VM\VirtualMachine.cs | 4148 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\EngineLoop.cs | 153 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\EnginePhase.cs | 71 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\EventLoop\EventLoopCoordinator.cs | 437 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\EventLoop\MicrotaskQueue.cs | 159 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\EventLoop\TaskQueue.cs | 215 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\ExecutionContext.cs | 162 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\FenEnvironment.cs | 568 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\FenFunction.cs | 448 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\FenObject.cs | 1836 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\FenRuntime.cs | 18142 | production-grade | This is the heart of the JS runtime and carries substantial built-in, realm, async, and compatibility logic; clearly one of the most complete files in the repo. |
| FenBrowser.FenEngine\Core\FenSymbol.cs | 198 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\FenValue.cs | 622 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\IDomBridge.cs | 23 | basic | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\InputQueue.cs | 427 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Interfaces\IExecutionContext.cs | 135 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Interfaces\IHistoryBridge.cs | 13 | basic | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Interfaces\IHtmlDdaObject.cs | 10 | basic | Intentionally empty marker interface for Annex B [[IsHTMLDDA]] semantics; thin by design rather than missing implementation. |
| FenBrowser.FenEngine\Core\Interfaces\IModuleLoader.cs | 23 | basic | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Interfaces\IObject.cs | 79 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Interfaces\IValue.cs | 94 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\JsThrownValueException.cs | 65 | basic | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Lexer.cs | 2043 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\ModuleLoader.cs | 799 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Parser.cs | 7237 | production-grade | Very large ECMAScript parser with deep grammar coverage, precedence handling, and recovery logic; high implementation depth despite the maintenance burden. |
| FenBrowser.FenEngine\Core\PropertyDescriptor.cs | 90 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Types\JsBigInt.cs | 214 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsIntl.cs | 2071 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsMap.cs | 156 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Types\JsPromise.cs | 658 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsSet.cs | 224 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsSymbol.cs | 196 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsTypedArray.cs | 1500 | production-grade | Engine core/runtime/compiler/VM logic with substantial execution semantics. |
| FenBrowser.FenEngine\Core\Types\JsWeakMap.cs | 153 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Types\JsWeakSet.cs | 133 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Types\ModuleNamespaceObject.cs | 142 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\Core\Types\Shape.cs | 86 | partial | Engine core helper/contract/support type; important, but narrower than the main runtime files. |
| FenBrowser.FenEngine\DevTools\DebugConfig.cs | 136 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\DevTools\DevToolsCore.cs | 1081 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\DOM\AttrWrapper.cs | 270 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\CommentWrapper.cs | 40 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\CustomElementRegistry.cs | 782 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\CustomEvent.cs | 71 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\DocumentWrapper.cs | 965 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\DomEvent.cs | 359 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\DomMutationQueue.cs | 179 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\DomWrapperFactory.cs | 47 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\ElementWrapper.cs | 3910 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\EventListenerRegistry.cs | 328 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\EventTarget.cs | 695 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\FontLoadingBindings.cs | 614 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\HighlightApiBindings.cs | 1522 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\HTMLCollectionWrapper.cs | 497 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\InMemoryCookieStore.cs | 224 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\LegacyUiEvents.cs | 267 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\MutationObserverWrapper.cs | 222 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\NamedNodeMapWrapper.cs | 343 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\NodeListWrapper.cs | 228 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\NodeWrapper.cs | 564 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\Observers.cs | 269 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\RangeWrapper.cs | 317 | production-grade | Core DOM model/algorithm file with substantial structural behavior. |
| FenBrowser.FenEngine\DOM\ShadowRootWrapper.cs | 89 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\StaticNodeList.cs | 26 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\TextWrapper.cs | 60 | basic | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\DOM\TouchEvent.cs | 167 | partial | DOM support or contract file; important for model shape, but intentionally narrower in scope. |
| FenBrowser.FenEngine\Errors\FenError.cs | 137 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\FenBrowser.FenEngine.csproj | 52 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.FenEngine\HTML\ForeignContent.cs | 334 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\HTML\HtmlEntities.cs | 483 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\HTML\HtmlToken.cs | 102 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\HTML\HtmlTokenizer.cs | 1547 | production-grade | A second HTML tokenizer stack inside FenEngine. It is functional, but its existence duplicates ownership already present in FenBrowser.Core. |
| FenBrowser.FenEngine\HTML\HtmlTreeBuilder.cs | 1562 | production-grade | A second HTML tree-builder inside FenEngine. Real code exists, but duplicated parser ownership weakens architectural clarity. |
| FenBrowser.FenEngine\Interaction\FocusManager.cs | 123 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Interaction\HitTestResult.cs | 98 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Interaction\InputManager.cs | 247 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Jit\BytecodeCompiler.cs | 427 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\Jit\FenBytecode.cs | 88 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Jit\JitCompiler.cs | 464 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\Jit\JitRuntime.cs | 66 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Layout\AbsolutePositionSolver.cs | 395 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Algorithms\BlockLayoutAlgorithm.cs | 701 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\Algorithms\ILayoutAlgorithm.cs | 21 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Algorithms\LayoutContext.cs | 44 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Algorithms\LayoutHelpers.cs | 111 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\BoxModel.cs | 139 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\ContainingBlockResolver.cs | 251 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Contexts\BlockFormattingContext.cs | 730 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\Contexts\FlexFormattingContext.cs | 1793 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\Contexts\FloatManager.cs | 99 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Contexts\FormattingContext.cs | 142 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Contexts\GridFormattingContext.cs | 289 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Contexts\InlineFormattingContext.cs | 1144 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\Contexts\LayoutBoxOps.cs | 180 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Contexts\LayoutState.cs | 58 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Coordinates\LogicalTypes.cs | 91 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Coordinates\WritingModeConverter.cs | 109 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\FloatExclusion.cs | 192 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\GridLayoutComputer.Areas.cs | 72 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\GridLayoutComputer.cs | 1105 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\GridLayoutComputer.Parsing.cs | 277 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\ILayoutComputer.cs | 74 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\InlineLayoutComputer.cs | 1115 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\InlineRunDebugger.cs | 275 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutContext.cs | 104 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutEngine.cs | 462 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutHelper.cs | 245 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutPositioningLogic.cs | 207 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutResult.cs | 141 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\LayoutValidator.cs | 125 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\MarginCollapseComputer.cs | 192 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\MarginCollapseTracker.cs | 161 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\MinimalLayoutComputer.cs | 3132 | production-grade | Large layout engine with box model, pseudo handling, float/BFC tracking, and inline/table logic; clearly real engine work, not a sketch. |
| FenBrowser.FenEngine\Layout\MultiColumnLayoutComputer.cs | 156 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\PseudoBox.cs | 589 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\ReplacedElementSizing.cs | 323 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\ScrollAnchoring.cs | 69 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\TableLayoutComputer.cs | 596 | production-grade | Layout algorithm/context code with real box-flow behavior. |
| FenBrowser.FenEngine\Layout\TextLayoutComputer.cs | 293 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\TextLayoutHelper.cs | 243 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\TransformParsed.cs | 20 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Tree\BoxTreeBuilder.cs | 379 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Tree\LayoutBox.cs | 83 | partial | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Layout\Tree\LayoutNodeTypes.cs | 62 | basic | Layout helper/context/support file that contributes to the engine, but is narrower in standalone scope. |
| FenBrowser.FenEngine\Observers\ObserverCoordinator.cs | 848 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\Program.cs | 205 | basic | Engine-side CLI/test harness entry point. Useful for internal verification, but it is not product browser runtime and mixes debug/test responsibilities into the main project. |
| FenBrowser.FenEngine\Rendering\Backends\HeadlessRenderBackend.cs | 209 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Backends\SkiaRenderBackend.cs | 464 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\BidiResolver.cs | 51 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\BidiTextRenderer.cs | 478 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\BrowserApi.cs | 4197 | partial | Huge browser orchestration surface tying navigation, loading, scripting, rendering, and WPT remapping together; powerful but boundary-heavy. |
| FenBrowser.FenEngine\Rendering\BrowserCoreHelpers.cs | 43 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\BrowserEngine.cs | 82 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\BaseFrameReusePolicy.cs | 40 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\DamageRasterizationPolicy.cs | 83 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\DamageRegionNormalizationPolicy.cs | 164 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\FrameBudgetAdaptivePolicy.cs | 104 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\PaintCompositingStabilityController.cs | 75 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\PaintDamageTracker.cs | 151 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Compositing\ScrollDamageComputer.cs | 166 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Core\ILayoutEngine.cs | 20 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Core\RenderContext.cs | 35 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Css\CascadeEngine.cs | 771 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssAnimationEngine.cs | 1038 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssClipPathParser.cs | 261 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssEngineFactory.cs | 121 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssFilterParser.cs | 344 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssFlexLayout.cs | 825 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssFloatLayout.cs | 414 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssFunctions.cs | 762 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssGridAdvanced.cs | 696 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssLoader.cs | 6390 | partial | Main CSS loading/cascade engine with caching, matching, pseudo support, and media handling; this carries most real CSS behavior. |
| FenBrowser.FenEngine\Rendering\Css\CssLoaderValueParsing.cs | 401 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssModel.cs | 270 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssParser.cs | 1115 | partial | Despite the name, this is a lean media/color utility after an earlier parser was removed; functional, but not a full parser subsystem by itself. |
| FenBrowser.FenEngine\Rendering\Css\CssSelectorAdvanced.cs | 579 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssSelectorParser.cs | 30 | basic | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssStyleApplicator.cs | 155 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssSyntaxParser.cs | 640 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssToken.cs | 109 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssTokenizer.cs | 617 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssTransform3D.cs | 493 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\CssValue.cs | 286 | partial | CSS support/helper/config surface; useful, but not a complete styling subsystem on its own. |
| FenBrowser.FenEngine\Rendering\Css\CssValueParser.cs | 634 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\Css\SelectorMatcher.cs | 1165 | production-grade | CSS subsystem file with meaningful cascade/style/layout contribution. |
| FenBrowser.FenEngine\Rendering\CssTransitionEngine.cs | 339 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\CustomHtmlEngine.cs | 2426 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\DebugOverlay.cs | 235 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\ElementStateManager.cs | 602 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\ErrorPageRenderer.cs | 240 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\FontRegistry.cs | 537 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\HistoryEntry.cs | 20 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\ImageLoader.cs | 1149 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Interaction\HitTester.cs | 310 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Interaction\ScrollbarRenderer.cs | 335 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Interaction\ScrollManager.cs | 748 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\IRenderBackend.cs | 226 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\LiteDomUtil.cs | 24 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\NavigationManager.cs | 179 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\NewTabRenderer.cs | 183 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Paint\LayoutTreeDumper.cs | 324 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Paint\PaintDebugOverlay.cs | 263 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Painting\BoxPainter.cs | 436 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Painting\DisplayList.cs | 44 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Painting\DisplayListBuilder.cs | 247 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Painting\ImagePainter.cs | 268 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Painting\Painter.cs | 312 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Painting\StackingContextComplete.cs | 500 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Painting\StackingContextPainter.cs | 364 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Painting\TextPainter.cs | 445 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\PaintTree\ImmutablePaintTree.cs | 176 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\IPaintNodeVisitor.cs | 23 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\NewPaintTreeBuilder.cs | 4040 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\PaintTree\PaintNode.cs | 173 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\PaintNodeBase.cs | 330 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\PaintTree\PaintTree.cs | 89 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\PaintTreeBuilder.cs | 370 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\PaintTreeDiff.cs | 27 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\PaintTree\PositionedGlyph.cs | 34 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\Performance\IncrementalLayout.cs | 236 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\Performance\ParallelPainter.cs | 337 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\RenderCommands.cs | 570 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\RenderDataTypes.cs | 129 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RendererSafetyPolicy.cs | 21 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RenderPipeline.cs | 151 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\RenderTree\FrameState.cs | 77 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RenderTree\RectExtensions.cs | 34 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RenderTree\RenderBox.cs | 644 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\RenderTree\RenderObject.cs | 57 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RenderTree\RenderText.cs | 39 | basic | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\RenderTree\ScrollModel.cs | 209 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\ResponsiveImageSourceSelector.cs | 110 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\SkiaDomRenderer.cs | 851 | partial | Functional renderer bridge with damage tracking and layout/paint orchestration, but the file explicitly describes itself as transitional adapter debt. |
| FenBrowser.FenEngine\Rendering\SkiaRenderer.cs | 1086 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\StackingContext.cs | 117 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\SvgRenderer.cs | 374 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\UserAgent\UAStyleProvider.cs | 367 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\WebGL\WebGL2RenderingContext.cs | 645 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Rendering\WebGL\WebGLConstants.cs | 278 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\WebGL\WebGLContextManager.cs | 285 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\WebGL\WebGLObjects.cs | 279 | partial | Rendering helper/glue/compatibility file; useful, but not the primary engine center of gravity. |
| FenBrowser.FenEngine\Rendering\WebGL\WebGLRenderingContext.cs | 1216 | production-grade | Rendering/painting/compositing implementation with substantial visual behavior. |
| FenBrowser.FenEngine\Resources\ua.css | 322 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Scripting\CanvasRenderingContext2D.cs | 1120 | production-grade | Browser-facing scripting bridge with real DOM/API/runtime integration. |
| FenBrowser.FenEngine\Scripting\JavaScriptEngine.cs | 4349 | production-grade | Real browser-hosted JS engine bridge with permissions, event loop, DOM hooks, and runtime orchestration; broad and operational. |
| FenBrowser.FenEngine\Scripting\JavaScriptEngine.Dom.cs | 1634 | production-grade | Browser-facing scripting bridge with real DOM/API/runtime integration. |
| FenBrowser.FenEngine\Scripting\JavaScriptEngine.Execution.cs | 358 | production-grade | Browser-facing scripting bridge with real DOM/API/runtime integration. |
| FenBrowser.FenEngine\Scripting\JavaScriptEngine.Methods.cs | 242 | production-grade | Browser-facing scripting bridge with real DOM/API/runtime integration. |
| FenBrowser.FenEngine\Scripting\JavaScriptRuntimeProfile.cs | 37 | basic | Scripting support or focused API file; real behavior exists, but the surface is narrower. |
| FenBrowser.FenEngine\Scripting\JsRuntimeAbstraction.cs | 128 | partial | Scripting support or focused API file; real behavior exists, but the surface is narrower. |
| FenBrowser.FenEngine\Scripting\ProxyAPI.cs | 178 | partial | Scripting support or focused API file; real behavior exists, but the surface is narrower. |
| FenBrowser.FenEngine\Scripting\ReflectAPI.cs | 276 | partial | Scripting support or focused API file; real behavior exists, but the surface is narrower. |
| FenBrowser.FenEngine\Security\IPermissionManager.cs | 120 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.FenEngine\Security\IResourceLimits.cs | 128 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.FenEngine\Security\PermissionManager.cs | 124 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.FenEngine\Security\PermissionStore.cs | 106 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.FenEngine\smoke_tests.js | 19 | basic | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Storage\FileStorageBackend.cs | 488 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\Storage\InMemoryStorageBackend.cs | 239 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.FenEngine\Storage\IStorageBackend.cs | 120 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Storage\StorageUtils.cs | 124 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\test_output.txt | 42 | basic | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\TestFenEngine.cs | 453 | production-grade | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Testing\AcidTestRunner.cs | 365 | partial | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Testing\Test262Runner.cs | 1830 | production-grade | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Testing\UsedValueComparator.cs | 387 | production-grade | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Testing\VerificationRunner.cs | 69 | basic | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Testing\WPTTestRunner.cs | 986 | production-grade | Embedded harness or test artifact inside the main engine project; real utility code, but not browser runtime product behavior. |
| FenBrowser.FenEngine\Typography\GlyphRun.cs | 76 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.FenEngine\Typography\IFontService.cs | 72 | basic | Thin support/configuration/contract file; appropriate for its role, but not a large standalone feature. |
| FenBrowser.FenEngine\Typography\NormalizedFontMetrics.cs | 141 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Typography\SkiaFontService.cs | 114 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\Typography\TextShaper.cs | 282 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.FenEngine\WebAPIs\BinaryDataApi.cs | 457 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\Cache.cs | 751 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\CacheStorage.cs | 437 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\FetchApi.cs | 1172 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\FetchEvent.cs | 254 | partial | Focused Web API or support surface; useful, but not a full browser subsystem on its own. |
| FenBrowser.FenEngine\WebAPIs\IndexedDBService.cs | 1499 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\IntersectionObserverAPI.cs | 292 | partial | Focused Web API or support surface; useful, but not a full browser subsystem on its own. |
| FenBrowser.FenEngine\WebAPIs\ResizeObserverAPI.cs | 88 | partial | Focused Web API or support surface; useful, but not a full browser subsystem on its own. |
| FenBrowser.FenEngine\WebAPIs\StorageApi.cs | 415 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\TestConsoleCapture.cs | 178 | partial | Focused Web API or support surface; useful, but not a full browser subsystem on its own. |
| FenBrowser.FenEngine\WebAPIs\TestHarnessAPI.cs | 327 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\WebAPIs.cs | 1217 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\WebAPIs\XMLHttpRequest.cs | 665 | production-grade | Web API implementation with async/stateful behavior exposed to page script. |
| FenBrowser.FenEngine\Workers\ServiceWorker.cs | 165 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\ServiceWorkerClients.cs | 123 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\ServiceWorkerContainer.cs | 278 | production-grade | Worker/service-worker runtime code with meaningful lifecycle and async behavior. |
| FenBrowser.FenEngine\Workers\ServiceWorkerGlobalScope.cs | 59 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\ServiceWorkerManager.cs | 614 | production-grade | Worker/service-worker runtime code with meaningful lifecycle and async behavior. |
| FenBrowser.FenEngine\Workers\ServiceWorkerRegistration.cs | 82 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\StructuredClone.cs | 389 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\WorkerConstructor.cs | 216 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\WorkerGlobalScope.cs | 430 | production-grade | Worker/service-worker runtime code with meaningful lifecycle and async behavior. |
| FenBrowser.FenEngine\Workers\WorkerPromise.cs | 87 | partial | Worker support/contract file; contributes to the subsystem, but is narrower in itself. |
| FenBrowser.FenEngine\Workers\WorkerRuntime.cs | 533 | production-grade | Worker/service-worker runtime code with meaningful lifecycle and async behavior. |

## FenBrowser.Host
Host is operational and feature-rich, especially around integration, widgets, and process coordination. The boundary is less clean than it should be because startup, automation, testing modes, and process roles are mixed into the same project entry surface.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.Host\app.manifest | 19 | basic | Windows application manifest; packaging and OS capability metadata, not executable browser logic. |
| FenBrowser.Host\BrowserIntegration.cs | 1655 | production-grade | High-value glue layer between BrowserHost, renderer, events, remote frames, and repaint scheduling; real functionality with some placeholder/render-fallback seams. |
| FenBrowser.Host\ChromeManager.cs | 709 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Host\Compositor.cs | 305 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Host\Context\ContextMenuBuilder.cs | 101 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Context\ContextMenuItem.cs | 65 | basic | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\DevToolsHostAdapter.cs | 455 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |
| FenBrowser.Host\FenBrowser.Host.csproj | 53 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.Host\icon.ico | binary | basic | Binary host asset; included for completeness, but not part of the executable browser logic. |
| FenBrowser.Host\Input\CursorManager.cs | 109 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Input\FocusManager.cs | 106 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Input\InputManager.cs | 103 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Input\KeyboardDispatcher.cs | 90 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\ProcessIsolation\BrokeredProcessIsolationCoordinator.cs | 606 | production-grade | Concrete per-tab broker/process coordination with restart policy, assignment, and sandbox lifecycle handling; one of the stronger host-side subsystems. |
| FenBrowser.Host\ProcessIsolation\FrameSharedMemory.cs | 326 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\Fuzz\IpcFuzzHarness.cs | 536 | production-grade | Process-boundary/IPC/sandbox coordination code with real lifecycle handling. |
| FenBrowser.Host\ProcessIsolation\Gpu\GpuProcessIpc.cs | binary | stub | Empty file; this process boundary exists architecturally but not as implemented IPC code yet. |
| FenBrowser.Host\ProcessIsolation\InProcessIsolationCoordinator.cs | 52 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\IpcFuzzBaseline.cs | 226 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\IProcessIsolationCoordinator.cs | 29 | basic | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\Network\NetworkChildProcessHost.cs | 188 | partial | Network helper/contract/config code; valuable infrastructure, but not a full subsystem by itself. |
| FenBrowser.Host\ProcessIsolation\Network\NetworkProcessCoordinator.cs | 418 | production-grade | Networking/policy implementation with real transport, parsing, or security behavior. |
| FenBrowser.Host\ProcessIsolation\Network\NetworkProcessIpc.cs | 407 | production-grade | Networking/policy implementation with real transport, parsing, or security behavior. |
| FenBrowser.Host\ProcessIsolation\ProcessIsolationCoordinatorFactory.cs | 28 | basic | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\ProcessIsolationRuntime.cs | 99 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\RendererChildLoopIo.cs | 42 | basic | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\RendererInputEvent.cs | 29 | basic | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\RendererIpc.cs | 497 | production-grade | Process-boundary/IPC/sandbox coordination code with real lifecycle handling. |
| FenBrowser.Host\ProcessIsolation\Targets\TargetChildProcessHost.cs | 222 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\Targets\TargetProcessIpc.cs | 299 | partial | Process-isolation support/glue or contract surface; direction is clear, but standalone depth is limited. |
| FenBrowser.Host\ProcessIsolation\Utility\UtilityProcessIpc.cs | binary | stub | Empty file; utility-process IPC is still a placeholder seam rather than shipped behavior. |
| FenBrowser.Host\Program.cs | 1440 | partial | Main host entry point is operational, but it also owns renderer child, network child, WebDriver, Test262, WPT, and Acid2 startup flows, which makes the boundary noisy. |
| FenBrowser.Host\RootWidget.cs | 178 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.Host\Tabs\BrowserTab.cs | 212 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Tabs\TabManager.cs | 202 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\Theme\ThemeManager.cs | 104 | partial | Host support layer or bridge file; useful, but narrower than the main browser loop. |
| FenBrowser.Host\WebDriver\FenBrowserDriver.cs | 414 | production-grade | Host-layer coordination code with real UI/runtime integration. |
| FenBrowser.Host\WebDriver\HostBrowserDriver.cs | 644 | production-grade | Host-layer coordination code with real UI/runtime integration. |
| FenBrowser.Host\Widgets\AddressBarWidget.cs | 673 | production-grade | Substantial user-facing widget with editing, selection, icons, autocomplete hooks, and painting logic; far beyond basic UI scaffolding. |
| FenBrowser.Host\Widgets\BookmarksBarWidget.cs | 167 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\ButtonWidget.cs | 202 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\ClipboardHelper.cs | 174 | production-grade | User-facing host widget with meaningful input/paint/layout behavior. |
| FenBrowser.Host\Widgets\ContextMenuWidget.cs | 299 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\DevToolsWidget.cs | 119 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\DockPanel.cs | 166 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\DropdownWidget.cs | 283 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\InspectorPopupWidget.cs | 186 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\SettingsPageWidget.cs | 1351 | production-grade | User-facing host widget with meaningful input/paint/layout behavior. |
| FenBrowser.Host\Widgets\SiteInfoPopupWidget.cs | 245 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\StackPanel.cs | 73 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\StatusBarWidget.cs | 192 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\SwitchWidget.cs | 133 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\TabBarWidget.cs | 389 | production-grade | User-facing host widget with meaningful input/paint/layout behavior. |
| FenBrowser.Host\Widgets\TabWidget.cs | 297 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\TextInputWidget.cs | 418 | production-grade | User-facing host widget with meaningful input/paint/layout behavior. |
| FenBrowser.Host\Widgets\ToolbarWidget.cs | 169 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\WebContentWidget.cs | 332 | partial | Host UI widget or helper; useful and real, but narrower in scope. |
| FenBrowser.Host\Widgets\Widget.cs | 374 | production-grade | User-facing host widget with meaningful input/paint/layout behavior. |
| FenBrowser.Host\WindowManager.cs | 421 | production-grade | Substantial implementation depth with real state handling, branching, and browser integration. |

## FenBrowser.DevTools
DevTools is meaningfully implemented: custom panels, protocol domains, and a remote debugging server all exist. The stack is usable, but some panel paths remain simplified and the feature depth is narrower than the engine itself.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.DevTools\Core\ColorPicker.cs | 128 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\CssAutocomplete.cs | 146 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\DevToolsController.cs | 536 | production-grade | DevTools infrastructure with real protocol/session/server behavior. |
| FenBrowser.DevTools\Core\DevToolsServer.cs | 168 | production-grade | DevTools infrastructure with real protocol/session/server behavior. |
| FenBrowser.DevTools\Core\DevToolsTheme.cs | 127 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\IDevToolsHost.cs | 172 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\IDevToolsPanel.cs | 178 | production-grade | DevTools infrastructure with real protocol/session/server behavior. |
| FenBrowser.DevTools\Core\NodeRegistry.cs | 164 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\Protocol\MessageRouter.cs | 147 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\Protocol\ProtocolMessage.cs | 148 | partial | DevTools controller/support code; functional, but narrower than the core protocol pieces. |
| FenBrowser.DevTools\Core\RemoteDebugServer.cs | 644 | partial | Real CDP/WebSocket server with auth token, heartbeat, queues, and HTTP endpoints; mature enough to be considered functional tooling. |
| FenBrowser.DevTools\Domains\CSSDomain.cs | 325 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DebuggerDomain.cs | 118 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DomDomain.cs | 612 | production-grade | Functional DOM inspection/editing protocol handler; good tooling depth for a custom DevTools stack. |
| FenBrowser.DevTools\Domains\DTOs\CssDtos.cs | 128 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DTOs\DebuggerDtos.cs | 58 | basic | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DTOs\DomNodeDto.cs | 210 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DTOs\NetworkDtos.cs | 145 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\DTOs\RuntimeDtos.cs | 148 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\NetworkDomain.cs | 121 | partial | DTO/domain-support code for DevTools protocol handling. |
| FenBrowser.DevTools\Domains\RuntimeDomain.cs | 573 | production-grade | Protocol domain implementation with real browser inspection/editing behavior. |
| FenBrowser.DevTools\FenBrowser.DevTools.csproj | 22 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.DevTools\Instrumentation\DomInstrumenter.cs | 208 | partial | Real behavior exists here, but the file is mostly glue, helper logic, generated surface, or a subsystem that is not fully exhaustive yet. |
| FenBrowser.DevTools\Panels\ConsolePanel.cs | 533 | production-grade | Substantial DevTools UI panel with real user-facing behavior. |
| FenBrowser.DevTools\Panels\ElementsPanel.cs | 2250 | partial | Large panel implementation with meaningful UI logic, but at least one child rendering path is still explicitly simplified/placeholder. |
| FenBrowser.DevTools\Panels\NetworkPanel.cs | 525 | production-grade | Substantial DevTools UI panel with real user-facing behavior. |
| FenBrowser.DevTools\Panels\SourcesPanel.cs | 382 | production-grade | Substantial DevTools UI panel with real user-facing behavior. |

## FenBrowser.WebDriver
WebDriver is mostly real: routing, sessions, security, and command dispatch are implemented, and the host bridge exists. It is held back mainly by a small amount of leftover scaffold code and by dependency on the completeness of the host backend.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.WebDriver\Class1.cs | 7 | stub | Leftover scaffold with no browser functionality; pure cleanup debt. |
| FenBrowser.WebDriver\CommandRouter.cs | 215 | partial | Broad W3C route map with command registration and path matching; good protocol coverage at the routing layer. |
| FenBrowser.WebDriver\Commands\CommandHandler.cs | 621 | production-grade | Large command-dispatch surface with security context setup and backend delegation; real implementation, though backend completeness still determines final behavior. |
| FenBrowser.WebDriver\Commands\ElementCommands.cs | 444 | production-grade | Command execution layer for WebDriver with real backend delegation. |
| FenBrowser.WebDriver\Commands\NavigationCommands.cs | 150 | partial | Focused WebDriver command/helper surface; real, but bounded. |
| FenBrowser.WebDriver\Commands\ScriptCommands.cs | 181 | partial | Focused WebDriver command/helper surface; real, but bounded. |
| FenBrowser.WebDriver\Commands\SessionCommands.cs | 107 | partial | Focused WebDriver command/helper surface; real, but bounded. |
| FenBrowser.WebDriver\Commands\WindowCommands.cs | 312 | production-grade | Command execution layer for WebDriver with real backend delegation. |
| FenBrowser.WebDriver\FenBrowser.WebDriver.csproj | 10 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.WebDriver\Protocol\Capabilities.cs | 154 | partial | Protocol/DTO/model code for WebDriver request and response handling. |
| FenBrowser.WebDriver\Protocol\ErrorCodes.cs | 83 | partial | Protocol/DTO/model code for WebDriver request and response handling. |
| FenBrowser.WebDriver\Protocol\WebDriverModels.cs | 105 | partial | Protocol/DTO/model code for WebDriver request and response handling. |
| FenBrowser.WebDriver\Protocol\WebDriverResponse.cs | 131 | partial | Protocol/DTO/model code for WebDriver request and response handling. |
| FenBrowser.WebDriver\Security\CapabilityGuard.cs | 122 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.WebDriver\Security\OriginValidator.cs | 113 | partial | Security helper or fallback abstraction; relevant, but not a full enforcement path on its own. |
| FenBrowser.WebDriver\Security\SandboxEnforcer.cs | 130 | production-grade | Security boundary/policy enforcement code with meaningful runtime impact. |
| FenBrowser.WebDriver\SessionManager.cs | 224 | partial | Solid session and element-reference manager; concise, but operational and properly isolated. |
| FenBrowser.WebDriver\WebDriverServer.cs | 316 | production-grade | Actual HTTP server with origin checks, routing, JSON handling, and error responses; this is real automation infrastructure. |

## FenBrowser.WebIdlGen
WebIdlGen is intentionally small and focused. It does one job cleanly: turning IDL into generated binding source.

| File | Lines | Rating | Audit Note |
|---|---:|---|---|
| FenBrowser.WebIdlGen\FenBrowser.WebIdlGen.csproj | 20 | basic | Project/build manifest; important for dependency shape, but this file itself is configuration rather than runtime logic. |
| FenBrowser.WebIdlGen\Program.cs | 113 | partial | Small but functional code-generation tool; narrow scope, yet it cleanly wires parser and generator into reproducible output. |


