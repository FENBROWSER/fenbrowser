# FenBrowser Codex - Volume II: The Core Foundation

**State as of:** 2026-03-30
**Codex Version:** 1.0

## 1. Overview

`FenBrowser.Core` represents the "Standard Library" of the browser. It is designed to be **engine-agnostic**; it defines _what_ a web page is (DOM, CSS values, Network resources) but not _how_ it is displayed (Resolution, Layout, Paint).

**Key Responsibilities:**

- **DOM Tree**: Hosting the state of the document (`Node`, `Element`).
- **Resource Management**: Fetching and caching HTTP resources.
- **Parsing**: Transforming raw bytes into DOM trees.
- **Base Types**: Defining geometric and stylistic primitives.

---

## 2. The DOM System (`FenBrowser.Core.Dom.V2`)

The Document Object Model (DOM) is the central data structure. FenBrowser uses a **Generation 2 (V2)** DOM implementation that is strictly compliant with the WHATWG Living Standard.

### 2.1 The Node Hierarchy (`Node.cs`)

All DOM objects inherit from the abstract `Node` class.

**Key Architecture Notes (V2):**

- **No Child List**: Unlike V1, the base `Node` class does not store children. Only `ContainerNode` subclasses (like `Element` and `Document`) have child lists, reducing memory footprint for leaf nodes like Text.
- **Root Node**: Implements `GetRootNode()` for Shadow DOM support.
- **Connectivity**: Tracks `IsConnected` state for lifecycle callbacks (`connectedCallback`).
- **Nested browsing-context documents (2026-07-03)**: `ContainerNode` preserves the owned `Document` identity when a parsed frame document is attached under an `iframe` / `frame` element. Frame descendants keep `OwnerDocument` pointing at the frame document instead of being adopted into the embedding page document.

### 2.2 Elements (`Element.cs`)

Extends `ContainerNode`. Represents HTML tags.

- **Attributes**: Managed via `NamedNodeMap` with security sanitization.
- **ClassList**: `DOMTokenList` implementation for efficient class toggling.
- **ShadowRoot**: Hooks for Shadow DOM encapsulation.
- **Shadow connectivity propagation (2026-04-07)**:
  - `Element.AttachShadow(...)`, `OnConnected()`, and `OnDisconnected()` now resynchronize `ShadowRoot` subtree connection state through the attached shadow tree.
  - This keeps `isConnected` correct for shadow-hosted descendants during attach/detach and removes a direct source of shadow-root DOM/WPT click and form-event failures.
- **Slot distribution hardening (2026-05-08)**:
  - `ShadowRoot` now maintains deterministic slot-assignment caches (slottable -> slot and slot -> assigned nodes) rebuilt from host light-DOM children.
  - Host child mutations and relevant attribute changes (`slot` on slottables, `name` on `<slot>`) now invalidate assignment caches.
  - `Element.AssignedSlot` / `Text.AssignedSlot` now resolve through the assignment cache and keep closed-root encapsulation (returns `null` when assignment is in a closed shadow tree).

### 2.3 Events & Mutation

- **EventTarget**: `Node` implements `AddEventListener` / `RemoveEventListener` (DOM Level 3).
- **MutationObserver**: Fully implemented.
  - `NotifyMutation()`: Called internally by `AppendChild`, `Remove`, etc.
  - `MutationRecord`: Queued for observers to process asynchronously.

---

## 3. Resource Management (`ResourceManager.cs`)

The `ResourceManager` is the gateway to the outside world. It handles HTTP requests, caching, and security checks.

### 3.1 Caching Strategy

The browser employs a **Sharded 2-Level Cache**:

1.  **L1 Memory Cache**: `ShardedCache<TextEntry>` and `ShardedCache<ImgEntry>`.
    - Partitioned by "Referer" to prevent cross-site leaks.
2.  **L2 Disk Cache**: Persisted to `Cache/` directory (hashed filenames).
    - Disabled in Private Mode.

### 3.2 Fetch Pipeline

1.  **Input**: URL, Referer, Accept Headers.
2.  **Checks**:
    - CSP (Content Security Policy) validation via `ActivePolicy`.
    - Local Schemes (`data:`, `file:`).
3.  **Network**: Uses `System.Net.Http.HttpClient` wrapped in `NetworkClient` with handlers:
    - `TrackingPreventionHandler`
    - `AdBlockHandler`
    - `HstsHandler`
    - **TLS policy**: `NetworkConfiguration.IgnoreCertificateErrors` (default: `false`) â€” production-safe; only set to `true` for deliberate lab diagnostics.
    - **Proxy policy**: `NetworkConfiguration.UseSystemProxy` (default: `true`) â€” respects OS-managed proxies for compliant corporate networks.
4.  **Processing**:
    - MIME Sniffing (`MimeSniffer.cs`) if the server sends generic types.
    - Encoding Detection using BOM or headers.

### 3.3 Verification Truth Hardening (2026-03-30)

- `ResourceManager` now registers authoritative source truth only for top-level document fetches. Subresource responses no longer overwrite the navigation-size/hash evidence that drives verification.
- `ContentVerifier` now resets state per navigation and distinguishes authoritative source/rendered registrations from provisional updates. This prevents late script/style/image activity from corrupting the main document verification summary.
- Workspace-root runtime artifacts now align with verifier accounting during the render/perf P1 closure run:
  - `logs/raw_source_20260330_131408.html`: `187710` bytes
  - verifier network result: `PASS (187527 bytes)`
  - `logs/rendered_text_20260330_131429.txt`: `517` characters
  - verifier visible-text result: `PASS (323 characters)`
- `DebugConfig` defaults were narrowed for production-truthful diagnostics:
  - deep per-node debug switches now default off
  - `LogFrameTiming` and `LogVerification` stay enabled
  - hot-path text-position logging now respects explicit layout-debug gating instead of always writing per-text records

---

## 4. The Parsing Engine (`FenBrowser.Core.Parsing`)

Based strictly on the **HTML5 Parsing Specification**.

### 4.0 Parser Hot-Path Logging Guard (2026-04-13)

- `HtmlTreeBuilder.ProcessToken(...)` no longer emits temporary token-level parse traces on the default path.
- The trace path is now gated by `DebugConfig.LogHtmlParse`, preserving targeted diagnostics without forcing high-volume `HtmlParsing` log I/O during normal navigation.

### 4.0.1 Canonical HTML Parsing Entrypoints (2026-04-21)

- `FenBrowser.Core/Parsing/HtmlParser.cs`
  - Added canonical parser entrypoints:
    - `ParseDocument(...)`
    - `ParseFragment(contextElement, markup, ...)`
    - `ParseStream(...)`
    - `ParseDocumentDetailed(...)` (returns document + parsing outcome + build metrics)
  - Unified entrypoints on a single tokenizer/tree-builder contract with shared `HtmlParsingOutcome` propagation.
  - Expanded `HtmlParserOptions` so document callers can optionally route through `PipelineContext` and parse checkpoint callbacks without bypassing the canonical parser API.
  - `ParseFragment(...)` now inherits base URI from the owner document when no explicit parser base is supplied, keeping fragment URL resolution consistent.
  - Removed non-spec SVG void/self-closing shortcuts from `HtmlParser.IsVoid(...)`.
- `FenBrowser.Core/StreamingHtmlParser.cs`
  - Primary async/incremental APIs now route through canonical parser entrypoints (`ParseDocument` / `ParseStream`) instead of bypass instance parsing paths.
- `FenBrowser.Core/Dom/V2/Element.cs`
  - `Element.InnerHTML` now uses canonical `HtmlParser.ParseFragment(...)` (no `<html><body>` wrapper parse path).
- `FenBrowser.Core/Dom/V2/ShadowRoot.cs`
  - `ShadowRoot.InnerHTML` now uses canonical `HtmlParser.ParseFragment(...)` with host-context fragment parsing.

### 4.0.2 JS Dependency Parser Hardening (2026-04-22)

- `FenBrowser.FenEngine/Core/Parser.cs` now parses assignment-expression contexts with strict delimiter boundaries in:
  - function default-parameter initializers,
  - object literal property values/defaults,
  - call arguments and array/object spread elements.
- This removes parse-state desynchronization that previously cascaded into `no prefix parse function for Comma/RBrace/RParen` on modern dependency scripts.
- Regression coverage now includes upstream WPT dependency sources:
  - `wpt/resources/testdriver.js`
  - `wpt/resources/testdriver-actions.js`
- Additional grammar regressions were added for:
  - async object methods with default parameters,
  - nested object literals with trailing commas/comments,
  - optional chaining/nullish coalescing in method-rich object initializers.

### 4.1 HtmlTokenizer

- Converts a stream of characters into `HtmlToken` objects (StartTag, EndTag, Character, Comment).
- **State Machine**: Handles intricacies like "RAWTEXT" (inside `<script>`) and "RCDATA" (inside `<textarea>`).
- **Character Reference Hardening (2026-02-26)**:
  - numeric references now resolve via code-point-safe conversion (`&#...;`, `&#x...;`).
  - common named references (`&amp;`, `&lt;`, `&gt;`, `&quot;`, `&apos;`, `&nbsp;`) now resolve in both text and attribute-value tokenizer states.
  - unknown named references remain literal text (no destructive consumption).
  - named-reference decoding now uses longest-match parsing and supports semicolon omission in text-safe boundaries (e.g. `&copy 2026`).
  - attribute-value semicolon-omission guard now preserves literal text when the next character is alphanumeric or `=` (e.g. `&copy=1`).
  - numeric reference Windows-1252 remap table is now applied for compatibility code points (`&#128;` -> `\u20AC`).
  - tokenizer now uses cached platform entity decode fallback for broader named-reference coverage (e.g. `&larr;`, `&sum;`) beyond the local hot-path map.
  - invalid numeric-reference starts now preserve literal consumed prefixes instead of dropping markers (`&#;` and `&#x;` remain literal in both text and attribute contexts).
  - legacy prefix behavior is preserved for compatibility (`&notanentity;` -> `Â¬anentity;`).
- **Tokenizer Safety Limits (2026-02-26)**:
  - `HtmlTokenizer` now exposes `MaxTokenEmissions` (default `2,000,000`) and force-emits EOF when the cap is reached to prevent pathological unbounded token streams.
- `HtmlParser` now accepts centralized `ParserSecurityPolicy` and applies tokenizer/open-elements limits at parser entrypoints.
- `HtmlParser` now aligns parsed document URL state by setting both `Document.URL` and `Document.BaseURI` from the active parser base URI, so detached nodes created from that document inherit correct URL-resolution context.
- `Document.CreateElementNS(...)` now validates qualified names and namespace/prefix combinations closely enough to reject malformed names and illegal namespace usage with the correct DOMException class for the Acid3 namespace tranche.
- **Comment-State Recovery Hardening (2026-04-09)**:
  - `HtmlTokenizer` now exits the HTML comment family of states without truncating the rest of the document stream, which restores parser continuity for long script-heavy pages that mix comments and later executable markup.
  - This unblocked full Acid3 source ingestion where the harness previously collapsed to an almost-empty DOM after the early comment/script region.

### 4.2 HtmlTreeBuilder

- Consumes tokens and constructs the DOM tree.
- **Insertion Modes**: Complex state machine managing where nodes are inserted.
  - `HandleInitial`
  - `HandleInHead`
  - `HandleInBody` (The most common state)
  - `HandleInTable` (Special "foster parenting" rules for misnested tables)
- **Error Handling**: Implements "quirks mode" behavior for malformed HTML (e.g., missing closing tags).
- **Structural Hardening (2026-02-26)**:
  - open-elements stack pops now route through guarded underflow-safe logic for malformed-token recovery paths.
  - `MaxOpenElementsDepth` (default `4096`) now auto-closes overflow stack entries after token processing to bound pathological deep nesting.
- **Stage Telemetry (2026-02-20)**:
  - `HtmlParseBuildMetrics` now exposes per-build parse timings and volume:
    - `TokenizingMs`, `ParsingMs`, `TokenCount`.
  - Checkpoint accounting is now first-class:
    - `TokenizingCheckpointCount`, `ParsingCheckpointCount`.
  - Document readiness milestone is measured:
    - `DocumentReadyTokenCount` (first parsed token index where `DocumentElement` is available).
  - Interleaved primary-parse metrics are now available:
    - `UsedInterleavedBuild`, `InterleavedTokenBatchSize`, `InterleavedBatchCount`.
  - Optional checkpoint callback contract for instrumentation:
    - `ParseCheckpointTokenInterval`
    - `ParseCheckpointCallback` (`HtmlParseCheckpoint` with phase + final marker).
  - Incremental document checkpoint callback is now available:
    - `ParseDocumentCheckpointCallback(Document, HtmlParseCheckpoint)`
    - fired on parsing checkpoints (including final) with the current document state.
  - Interleaved build mode:
    - `InterleavedTokenBatchSize` controls tokenize/parse batch handoff (`0` keeps legacy full-buffer parse).
    - Batch handoff preserves parser correctness path while allowing first-class incremental parse progress in the production parser.
  - Conformance guardrails:
    - Parser regression suites now compare baseline and interleaved parse outputs for pathological and large multi-batch documents.
- **Pipeline Stage Entry (2026-02-20)**:
  - `BuildWithPipelineStages(PipelineContext)` runs parse inside scoped frame and explicit stages:
    - `PipelineStage.Tokenizing`
    - `PipelineStage.Parsing`.
- **Raw-Text State Feedback Hardening (2026-03-07)**:
  - Non-interleaved `HtmlTreeBuilder` now consumes tokens incrementally instead of tokenizing the full document ahead of tree construction, so tokenizer state changes requested by tree-builder insertion logic take effect before script/style bodies are consumed.
  - Interleaved parse batches now flush immediately when raw-text / RCDATA start tags (`script`, `style`, `title`, `textarea`, `xmp`, `iframe`, `noembed`, `noframes`, `noscript`, `plaintext`) are observed, preventing same-batch script bodies from being tokenized in plain `Data` state.
  - This closed the concrete Google repro where inline script text (`document.fonts.load(...); for(var a=0;a<w.length;...)`) previously became bogus markup and produced `Invalid characters in attribute name` diagnostics.

### 4.3 StreamingHtmlParser

- **Chunk-Split Raw-Text Hardening (2026-03-07)**:
  - `StreamingHtmlParser` now tracks raw-text elements across chunk boundaries and only exits when the matching closing tag is fully recognized.
  - Split `</script>` boundaries no longer leak intermediate script bytes into the markup token stream.

---

## 5. CSS Types (`FenBrowser.Core.Css`)

This namespace defines the _computed_ values, not the parser (which is in `FenEngine`).

- **CssComputed**: A massive class holding the final resolved values for a node (consolidates legacy CssColor/CssLength).
- **Thickness**: Struct for margin/padding (Math/Thickness.cs).
- **CssCornerRadius**: Struct for border radius.

---

## 6. Comprehensive Source Encyclopedia

This section maps **every key file** in the Core library to its specific responsibilities and line ranges.

### 6.1 DOM V2 Subsystem (`FenBrowser.Core.Dom.V2`)

The core data structure, compliant with usage of `ContainerNode` separation.

#### `Node.cs` (Lines 1-586)

Abstract base class.

- **Lines 235-307**: **Dirty Flag System** (`MarkDirty`, `PropagateChildDirtyUp`). Vital for rendering optimization.
- **Lines 349-407**: **`CompareDocumentPosition`**: Bitmask comparison logic.
- **Lines 538-575**: **Traversal**: `Descendants` and `SelfAndDescendants` iterators.

#### `ContainerNode.cs` (Lines 1-913)

Base for nodes capable of having children (Document, Element).

- **Lines 95-103**: **Child Cache**: Invalidates optimizations (`ChildElementCount`) on mutation.
- **Lines 393-493**: **Mutation Logic**: Verified insertion/removal (`AppendChildInternal`, `RemoveChildInternal`).
- **Lines 528-547**: **Observer Notification**: Triggers `MutationObserver` callbacks for child list changes.

#### `Element.cs` (Lines 1-901)

Represents HTML tags.

- **Lines 156-327**: **Attribute Management**: `GetAttribute`, `SetAttribute` (with sanitization).
- **Parser Trust Path**: `SetAttributeUnsafe` is exposed for HTML tree-construction paths so parsed attributes preserve source semantics while script/runtime `SetAttribute` keeps sanitizer enforcement.
- **Lines 413-503**: **Mutation Hooks**: `OnAttributeValueChanged` notifies the engine of style changes.
- **Lines 509-527**: **Style Invalidation**: Manages `UpdateAncestorFilter` for CSS optimization.
- **InnerHTML Setter**: `InnerHTML` now parses HTML fragments and replaces child nodes with parsed DOM content (instead of text-only assignment), improving DOM/script interoperability.

#### `Document.cs` (Lines 1-654)

The root node.

- **Lines 320-331**: **`GetElementById`**: Entry point for ID lookups.
- **Lines 371-462**: **Factory Methods**: `CreateElement`, `CreateTextNode`, etc.
- **Lines 506-509**: **`NotifyTreeDirty`**: Global signal for layout/paint schedulers.

#### `EventTarget.cs` (Lines 1-664)

Base for all components dispatching events.

- **Lines 24-48**: **`AddEventListener`**: Thread-safe listener registration.
- **Lines 73-89**: **`DispatchEvent`**: Implements Capture/Target/Bubble phases.

#### `Range.cs` (Lines 1-805)

Represents a continuous fragment of the document.

- **Lines 63-156**: **Boundary Management**: `SetStart`, `SetEnd`.
- **Lines 244-261**: **`DeleteContents`**: Complex logic to remove nodes partially contained in the range.

#### `MutationObserver.cs` (Lines 1-506)

Asynchronous DOM mutation observation.

- **Lines 39-79**: **`Observe`**: Registers interest in specific mutation types (Attributes, ChildList).
- **Lines 137-160**: **`EnqueueRecord`**: Thread-safe queueing of changes.
- **Lines 436-483**: **Filter Logic**: `GetObserversForNotification`.

#### `NamedNodeMap.cs` (Lines 1-343)

Optimized attribute map storage for Elements.

#### `Attr.cs` (Lines 1-120)

Represents a DOM attribute node (Legacy DOM Level 1).

- Wraps the attribute data stored in `Element`.

#### `CharacterData.cs` (Lines 1-307)

Abstract base for `Text`, `Comment`, and `ProcessingInstruction`.

- **Lines 80-150**: **`AppendData`**, `DeleteData`, `ReplaceData`: String manipulation logic.

#### `Comment.cs` (Lines 1-45)

Represents HTML comments (`<!-- ... -->`).

#### `DOMTokenList.cs` (Lines 1-300)

Efficient `class` attribute parsing (Space-separated tokens).

- **Lines 40-80**: **`Add`**: Token insertion.
- **Lines 120-150**: **`Toggle`**: Conditional addition/removal.

#### `DocumentFragment.cs` (Lines 1-83)

Lightweight container for nodes (not part of the main tree).

#### `DocumentType.cs` (Lines 1-60)

Represents the `<!DOCTYPE html>` node.

#### `DomException.cs` (Lines 1-149)

Standard DOM error codes (`HierarchyRequestError`, `InvalidCharacterError`).

#### `NodeIterator.cs` (Lines 1-200) & `TreeWalker.cs` (Lines 1-250)

Traversal helpers for filtering and walking the DOM tree.

#### `NodeList.cs` (Lines 1-320)

Live or static collection of nodes (e.g., `childNodes`, `querySelectorAll`).

#### `ShadowRoot.cs` (Lines 1-180)

The root of a Shadow DOM subtree.

- **Lines 40-60**: **`Host`**: Link back to the shadow host element.
- **InnerHTML Setter**: `ShadowRoot.InnerHTML` now parses fragment HTML into shadow children, aligning behavior with document/element parsing expectations.

#### `Text.cs` (Lines 1-120)

Represents text content.

- **Lines 50-80**: **`SplitText`**: Divides a text node into two (used during normalization).

### 6.5 Supplemental Files (Gap Fill)

#### Core Types (`FenBrowser.Core.Css`)

- **CssComputed.cs**: The large class holding resolved CSS properties for a node. Consolidates legacy CssColor, CssLength, and CssPropertyNames.
- **CssCornerRadius.cs**: Struct for border-radius and CssLength values.



#### Helpers & Security (`FenBrowser.Core.Dom.V2`)

- **`Mixins.cs`**: Shared logic for `IParentNode` and `IChildNode`.
- **`NodeFlags.cs`**: Bitflags for optimizing node state checks (IsConnected, HasChildren).
- **`TreeScope.cs`**: Manages ID lookup boundaries (Document vs ShadowRoot).
- **`ProcessIsolation/RendererIsolationPolicies.cs`**: Handles origin-based assignment keys (replaces legacy Origin.cs).
- **`ProcessIsolation/RendererIsolationPolicies.cs`**: Isolation policy checks (replaces legacy SecurityGuard.cs).
- **`Security/AttributeSanitizer.cs`**: Validates attribute names/values; inline `on*` handler values are preserved by default for browser compatibility, with opt-in strict blocking via `BlockInlineEventHandlersInStrictMode`.
- **`FenBrowser.FenEngine/Rendering/Css/CssModel.cs`**: Internal CSS selector representation.
- **`Selectors/SelectorParser.cs`**: recursive descent parser for CSS selectors.
- **`Selectors/SimpleSelector.cs`**: selector primitive implementations (`:not/:is/:has/:nth-*`, attribute/type/id/class matching).
- **`HtmlToken.cs`**: Data class for the Tokenizer output.
- **`ICssParser.cs`**: Interface definition for the CSS engine.

### 6.6 Quick Reference: API Surface

bsystem (`FenBrowser.Core.Parsing`)

#### `HtmlTokenizer.cs` (Lines 1-1213)

The lexical analyzer.

- **Lines 120-1117**: **`NextToken`**: A massive state machine implementing the HTML5 tokenization spec.
- **Lines 1149-1154**: **`EmitCurrentTag`**: Flushes the buffer to a token.

#### `HtmlTreeBuilder.cs` (Lines 1-1603)

The syntactic analyzer (DOM Construction).

- **Lines 85-153**: **`ProcessToken`**: Main dispatcher.
- **Lines 510-746**: **`HandleInBody`**: The primary insertion mode logic.
- **Lines 1102-1197**: **`FosterParent`**: Error recovery for tables (moving content out of `<table>` if invalid).
- **Initial Mode Hardening**: DOCTYPE handling now sets `Document.Mode` (`NoQuirks`, `Quirks`, `LimitedQuirks`) using common WHATWG-compatible triggers.
- **Head Meta Handling**: `<meta charset>` and `<meta http-equiv="Content-Type"...charset=...>` now update `Document.CharacterSet` during parsing.
- **Template Mode Handling**: `InTemplate` now maps start tags to appropriate insertion modes (`InTable`, `InColumnGroup`, `InTableBody`, `InRow`, `InBody`) and reprocesses tokens using the template insertion-mode stack.
- **Template Close/Reset**: Closing `</template>` now uses a shared close path that pops template scope, clears active-formatting markers, updates template mode stack, and resets insertion mode from stack state.
- **Attribute Consistency**: Parsed HTML attributes are now assigned through `SetAttributeUnsafe` in tree-construction code so Core and Engine parser paths preserve equivalent raw attribute values (including inline handler/source text).
- **Initial Mode Quirks Default**: When parsing starts without a doctype token, `HandleInitial(...)` now sets `Document.Mode = Quirks` before reprocessing in `BeforeHtml`, aligning with HTML5 initial-mode error-recovery expectations for missing-doctype documents.

#### `StreamingHtmlParser.cs` (Lines 1-493)

The public API for parsing.

- **Lines 136-166**: **`ParseIncrementallyAsync`**: Handles network streams chunk-by-chunk.

### 6.3 Network & Resources (`FenBrowser.Core.Network`)

#### `ResourceManager.cs` (Lines 1-1028)

The central data fetcher.

- **Lines 187-457**: **`FetchTextAsync`**: Caching + Network + CSP checks pipeline.
- **Lines 708-855**: **`FetchImageAsync`**: Specialized bitmap pipeline.
- **Lines 919-961**: **`SendAsync`**: The low-level `HttpClient` wrapper.

#### `NetworkClient.cs` (Lines 1-307)

Optimized HTTP client wrapper.

- **Lines 41-92**: **`SendAsync`**: Connection pooling logic.
- **Lines 202-255**: **Statistics**: Tracks headers/bytes for developer tools.

#### `ResourcePrefetcher.cs` (Lines 1-503)

Handles `<link rel="preload">` and `prefetch`.

- **Lines 104-146**: **`PrefetchFromDomAsync`**: Scans DOM for hint tags.
- **Lines 280-340**: **`ExecutePrefetchAsync`**: Background low-priority fetcher.

#### `MimeSniffer.cs` (Lines 1-183)

Determines content type when headers are missing/wrong.

- **Lines 68-135**: **`SniffFromMagicBytes`**: Checks file signatures (e.g., JPEG, PNG, PDF headers).

### 6.4 Infrastructure (`FenBrowser.Core`)

#### `CacheManager.cs` (Lines 1-401)

Memory management.

- **Lines 151-179**: **`Trim`**: LRU eviction strategy when memory pressure is high.
- **Lines 250-400**: **`TabCachePartition`**: Isolates cache entries per tab (Privacy).

#### `ContentVerifier.cs` (Lines 1-230)

Verification aggregator for source-vs-render diagnostics.

- `RegisterRendered(...)` now preserves the maximum seen node/text metrics during a navigation cycle so later transient empty passes do not downgrade an already valid render verification.
- Text metrics are counted by `NodeType.Text` + `TextContent` (instead of concrete type assumptions), improving compatibility with mixed DOM wrapper paths.

#### `SandboxPolicy.cs` (Lines 1-68)

Security definitions.

- Defines feature flags (`Scripts`, `Network`, `DomMutation`) for restricting iframe/tab capabilities.
- Added baseline HTML iframe sandbox-token parsing and policy derivation:
  - parses `allow-scripts`, `allow-same-origin`, `allow-forms`, `allow-popups`, and top-navigation-related tokens into reusable `IframeSandboxFlags`
  - derives a feature-level `SandboxPolicy` for sandboxed iframe contexts so engine bridges can consistently gate script, storage, and navigation exposure.

_End of Volume II_

### 6.5 Quick Reference: API Surface

#### Element (`FenBrowser.Core.Dom.V2.Element`)

| Method                    | Description                                | Complexity        |
| :------------------------ | :----------------------------------------- | :---------------- |
| `GetAttribute(name)`      | Safe retrieval of attribute.               | O(1)              |
| `SetAttribute(name, val)` | Updates attribute and marks style dirty.   | O(N) (Observers)  |
| `AppendChild(node)`       | Adds node to end of child list.            | O(1)              |
| `QuerySelector(css)`      | Finds first matching descendant.           | O(Depth)          |
| `GetBoundingClientRect()` | Returns visual bounds (via Layout engine). | O(1)\* (if clean) |

### 6.7 Phase-2 Diagnostics Path Centralization (2026-02-18)

- `Logging/DiagnosticPaths.cs`
  - Added centralized diagnostics path resolver for runtime artifacts.
  - Added root vs logs path helpers and append helpers.
  - Supports environment override: `FEN_DIAGNOSTICS_DIR`.

- `Engine/PhaseGuard.cs`
  - Replaced machine-specific hardcoded debug log path with `DiagnosticPaths.AppendRootText(...)`.

### 6.8 Phase-5 Runtime Policy Wiring (2026-02-18)

- `Network/Handlers/PrivacyHandler.cs`
  - `DNT` header behavior now follows `BrowserSettings.SendDoNotTrack` at request time instead of unconditional injection.
  - When DNT is disabled by user policy, any existing `DNT` header is removed before send.

- `ResourceManager.cs`
  - Added one-time policy-binding diagnostics at startup (`[PolicyBindings]`):
    - logs runtime-enforced toggles currently bound into network flow
    - logs UI-visible toggles that remain pending full runtime enforcement.

### 6.9 Phase-5 Runtime Policy Wiring - Tranche B (2026-02-18)

- `Network/Handlers/SafeBrowsingHandler.cs`
  - Added a runtime URL safety gate (known-dangerous host deny-list path) behind `BrowserSettings.SafeBrowsing`.

- `ResourceManager.cs`
  - Added `SafeBrowsingHandler` into the active network handler pipeline.
  - `ImproveBrowser` now controls high-entropy client-hints request headers at runtime; low-entropy `Sec-CH-UA`, `Sec-CH-UA-Mobile`, and `Sec-CH-UA-Platform` remain part of the normal browser request surface.
  - Policy diagnostics now report `UseSecureDNS` as pending while enforced toggles include `SafeBrowsing`/`ImproveBrowser`/`BlockPopups`.

### 6.10 Remaining Findings Tranche - CSP/Navigation Policy (2026-02-19)

- `Security/CspPolicy.cs`
  - Added origin-aware `IsAllowed(...)` overloads so `'self'` checks receive explicit origin context.

- `ResourceManager.cs`
  - CSP checks now pass origin context across text/image/bytes/generic-request paths.
  - Policy diagnostics now include file-navigation settings:
    - `AllowFileSchemeNavigation`
    - `AllowAutomationFileNavigation`.

- `BrowserSettings.cs`
  - Added runtime policy toggles for hardened file navigation behavior:
    - `AllowFileSchemeNavigation` (global gate)
    - `AllowAutomationFileNavigation` (automation override).

### 6.11 Phase-Completion Tranche - Secure DNS Runtime Wiring (2026-02-19)

- `Network/SecureDnsResolver.cs`
  - Added DNS-over-HTTPS resolver path with response parsing and short TTL cache.
  - Endpoint sources:
    - `BrowserSettings.SecureDnsEndpoint`
    - `FEN_SECURE_DNS_ENDPOINT` (env override).

- `Network/HttpClientFactory.cs`
  - Migrated transport handler to `SocketsHttpHandler`.
  - Added secure DNS `ConnectCallback` path that resolves hostnames via DoH when `UseSecureDNS=true`.
  - Added certificate validation callback configuration helper for host-side security state capture.

- `ResourceManager.cs`
  - Policy diagnostics now classify `UseSecureDNS` as runtime-enforced instead of pending.

### 6.12 Eight-Gap Closure Tranche - CSP Worker Directive Wiring (2026-02-19)

- `ResourceManager.cs`
  - Updated `worker` destination CSP mapping to prefer `worker-src` when present, with `child-src` fallback.
  - This aligns worker script fetch checks with modern CSP directive precedence.

### 6.13 Pipeline Context Hardening (2026-02-20)

- `Engine/PipelineContext.cs`
  - Added scoped lifecycle APIs:
    - `BeginScopedFrame()`
    - `BeginScopedStage(PipelineStage stage)`
  - These guarantee `EndFrame()`/`EndStage()` via `IDisposable`, including exception paths.
  - Stage timing now records real stage duration (stage-start to stage-end), not cumulative frame time.
  - Normalized transition diagnostics string to ASCII (`->`) for log stability.

### 6.14 Process Isolation Policy Primitives (2026-02-20)

- `ProcessIsolation/RendererIsolationPolicies.cs` (new)
  - Added origin-assignment policy utilities used by host brokered process model:
    - stable assignment-key derivation for `http/https` origins
    - explicit assignment classes for local/opaque navigations (`file://local`, `about://*`, `opaque://<scheme>`)
    - assignment-change detection helper for renderer recycle decisions.
  - Added restart policy primitive for renderer crash handling:
    - bounded restart budget
    - exponential backoff with max-delay cap
    - stable-session runtime reset and crash-loop quarantine controls.
  - Added registry-backed isolation state machine:
    - `RendererTabIsolationRegistry`
    - central navigation assignment decisions
    - expected/stale/unexpected exit classification
    - restart-plan/replay decision generation
    - start gating via `CanStartSession(...)` (quarantine + closed-tab enforcement).

- `Tests/Core/RendererIsolationPoliciesTests.cs` (new)
  - Added regression coverage for:
    - assignment-key normalization and reassignment decisions
    - unsupported URL handling
    - restart budget and backoff policy behavior.
  - Expanded coverage for registry decisions:
    - first navigation assignment
    - cross-origin reassignment triggers
    - expected/stale exit non-restart paths
    - restart budget exhaustion and shutdown/closed-tab invariants
    - stable-runtime restart reset
    - crash-loop quarantine and user-input quarantine release.

### 6.15 Navigation Lifecycle State Machine (2026-02-20)

- `Engine/NavigationLifecycle.cs` (new)
  - Added deterministic top-level navigation lifecycle primitives:
    - `NavigationLifecyclePhase` (`Requested`, `Fetching`, `ResponseReceived`, `Committing`, `Interactive`, `Complete`, `Failed`, `Cancelled`)
    - `NavigationLifecycleTransition` payload
    - `NavigationLifecycleSnapshot` state export
    - `NavigationLifecycleTracker` with monotonic transition enforcement.
  - Transition engine rejects invalid order and stale navigation ids by design.
  - Tracker emits lifecycle transitions for host/engine consumers through `Transitioned` event.

- `Tests/Core/NavigationLifecycleTrackerTests.cs` (new)
  - Added regression coverage for:
    - deterministic successful phase ordering
    - invalid transition rejection
    - stale navigation-id isolation
    - cancellation terminal behavior.

### 6.16 Navigation Lifecycle Tranche NL-2 (2026-02-20)

- `ResourceManager.cs`
  - `FetchResult` now carries redirect metadata from the canonical fetch pipeline:
    - `Redirected`
    - `RedirectCount`
    - `RedirectChain`
  - Redirect metadata is populated across success and error returns for deterministic lifecycle correlation.

- `Engine/NavigationLifecycle.cs`
  - Lifecycle snapshot/transition payloads now include:
    - redirect classification (`IsRedirect`, `RedirectCount`)
    - commit-source tag (`CommitSource`)
  - Added overload path to mark response metadata and commit source without out-of-band host heuristics.

- `Tests/Core/NavigationLifecycleTrackerTests.cs`
  - Added metadata regression:
    - redirect + commit-source values survive response/commit/complete lifecycle progression.

### 6.17 Navigation Lifecycle Tranche NL-4 (2026-02-20)

- `Engine/NavigationSubresourceTracker.cs` (new)
  - Added navigation-scoped pending subresource tracker:
    - pending count keyed by navigation id
    - explicit navigation reset/abandon operations
    - pending-count event channel (`PendingCountChanged`) for lifecycle completion gates.

- `Tests/Core/NavigationSubresourceTrackerTests.cs` (new)
  - Added regression coverage for:
    - per-navigation pending isolation
    - abandon cleanup behavior
    - navigation-id tagged pending-count events.

### 6.18 Selector Engine Conformance Tranche CSS-1 (2026-02-20)

- `Dom/V2/Selectors/SimpleSelector.cs`
  - `NthChildSelector` now supports Selectors-4 `of <selector-list>` filtering.
  - `NthChildSelector` specificity now includes selector-list specificity contributions when `of` is present.

- `Dom/V2/Selectors/SelectorParser.cs`
  - Attribute selector parser now accepts explicit `s` flag (case-sensitive override) in addition to `i`.

- `Tests/DOM/SelectorEngineConformanceTests.cs` (new)
  - Added regression coverage for:
    - `Element.Matches(...)` with `nth-child(... of ...)`
    - attribute flag behavior (`i`/`s`) in core selector path.

### 6.19 PAL Shared-Memory Productionization (2026-03-07)

- `Platform/CrossPlatformSharedMemoryRegion.cs` (new)
  - Added a cross-platform `ISharedMemoryRegion` backed by deterministic temp-directory files plus `MemoryMappedFile`.
  - Logical region names are hashed into stable file paths so cooperating processes on Linux/macOS can reopen the same region by name.
  - Read, write, span-write, pointer access, and bounds validation now mirror the Windows shared-memory contract.

- `Platform/PosixPlatformLayer.cs` (new)
  - Added a concrete Linux/macOS `IPlatformLayer`.
  - `CreateSharedMemory(...)` / `OpenSharedMemory(...)` now use the cross-platform mapped-file region instead of unsupported-platform exceptions.
  - `ApplySandbox(...)` now preserves direct-spawn PAL semantics (`UseShellExecute=false`, `CreateNoWindow` when desktop access is denied).
  - `GetProcessMemoryBytes(...)` now provides real process working-set queries on non-Windows hosts.
  - Sandbox factory remains explicitly null-sandbox backed on non-Windows hosts; this tranche removes PAL/IPC throw-path fallback, not the remaining OS-native sandbox gap.

- `Platform/PlatformLayerFactory.cs`
  - Linux and macOS now resolve to `PosixPlatformLayer` instead of the previous unsupported-platform fallback for baseline PAL operations.

- Net effect:
  - Non-Windows hosts now have working shared-memory/process PAL behavior for IPC baselines instead of immediately failing on named shared-memory creation/open.

### 6.20 POSIX Sandbox Launcher Integration (2026-03-07)

- `Security/Sandbox/Posix/PosixOsSandboxFactory.cs` (new)
  - Added a POSIX sandbox factory that resolves native launcher helpers on the current host:
    - Linux: `bwrap`
    - macOS: `sandbox-exec`
  - Broker profile still resolves to `NullSandbox`; non-broker child profiles now receive a command-backed sandbox when a native helper is present.

- `Security/Sandbox/Posix/PosixCommandSandbox.cs` (new)
  - Added helper-backed custom-spawn sandbox implementation.
  - Linux launches child processes through `bwrap` with namespace/session isolation and capability-shaped networking/file-write allowances.
  - macOS launches child processes through `sandbox-exec` with generated profile text shaped by granted capabilities.
  - Sandbox health now reports active-process count and aggregate working-set usage for spawned child processes.

- `Platform/PosixPlatformLayer.cs`
  - Non-Windows PAL now returns `PosixOsSandboxFactory` instead of unconditional `NullSandboxFactory`.

- Net effect:
  - Linux/macOS no longer hard-code all child-process sandbox creation to null-sandbox behavior when native launcher support is available on the host.

### 1.29 POSIX Sandbox Capability Enforcement Hardening (2026-03-07)
- `Security/Sandbox/Posix/PosixCommandSandbox.cs`
- POSIX helper-backed sandbox launch is now capability-shaped instead of using a broad read-everything wrapper policy.
- Linux `bwrap` launch generation now:
  - binds system runtime directories read-only,
  - binds the child executable directory and working directory explicitly instead of `ro-bind / /`,
  - exposes user-home and temp paths only when the profile capabilities require them,
  - shares the network namespace only when outbound/listen capabilities are granted,
  - sanitizes inherited environment variables through a narrow allowlist.
- macOS `sandbox-exec` profile generation now:
  - uses path-scoped `file-read*` / `file-write*` allowances for runtime/system/working paths,
  - denies `process-fork` unless child spawning is explicitly allowed by the profile,
  - only enables network inbound/outbound rules when granted by capability flags.
- POSIX sandboxing remains helper-backed and best-effort: there is still no seccomp/App Sandbox entitlement layer or self-restriction path for already-running processes.

### 1.30 Referrer-Policy Enforcement Hardening (2026-03-07)
- `ResourceManager.cs`
- Added shared `Referrer-Policy` parsing and outgoing `Referer` computation in the network/resource pipeline instead of unconditional `Referer: <full URL>` emission at each request call site.
- `ResourceManager` now parses response `Referrer-Policy` headers, adopts document/iframe navigation policy as the active page policy, and applies that policy across text, image, and generic byte fetches.
- Supported policy directives now include:
  - `no-referrer`
  - `no-referrer-when-downgrade`
  - `same-origin`
  - `origin`
  - `strict-origin`
  - `origin-when-cross-origin`
  - `strict-origin-when-cross-origin`
  - `unsafe-url`
- Navigation fetch results now also expose the parsed referrer policy on `FetchResult`, aligning header parsing with the existing `X-Frame-Options` response metadata path.

### 1.31 X-Frame-Options Enforcement Hardening (2026-03-07)
- `ResourceManager.cs`
- Frame-document fetches (`secFetchDest=iframe`) now enforce parsed `X-Frame-Options` response policy instead of only recording the header for later diagnostics.
- `DENY` now blocks all iframe document embeddings, `SAMEORIGIN` now requires the embedding document and framed document to share origin, and `ALLOW-FROM` now requires the embedding document origin to match the declared allowed origin.
- Violating iframe fetches now return a blocked `FetchResult` with preserved response metadata and increment the blocked-request telemetry counter, making the security decision visible to diagnostics/UI state.
- Browser-side XFO state in `BrowserApi` is now diagnostic-only; enforcement lives in the centralized network/resource fetch path where the frame response is available.

### 1.32 Mixed-Content Image Blocking Hardening (2026-03-07)
- `ResourceManager.cs`
- Hardened centralized image fetch entry points so secure documents no longer load insecure `http:` image subresources through engine-managed resource fetching.
- `FetchImageAsync(...)` and `FetchBytesAsync(..., secFetchDest: "image")` now block mixed-content image requests when the embedding/referrer document is `https:`.
- Blocked mixed-content image requests now increment blocked-request telemetry and emit explicit network-category warnings for diagnostics.

### 1.33 CORS Preflight Enforcement Hardening (2026-03-07)
- `Network/Handlers/CorsHandler.cs`
- `ResourceManager.cs`
- Centralized generic request dispatch in `ResourceManager.SendAsync(...)` now performs real CORS preflight for cross-origin non-simple requests before the actual request is sent.
- `CorsHandler` now classifies safelisted methods/headers, derives request origin from `Origin`/`Referrer`, computes unsafe request-header sets, and validates preflight responses against:
  - `Access-Control-Allow-Origin`
  - `Access-Control-Allow-Methods`
  - `Access-Control-Allow-Headers`
- Failed preflights now block the primary request with a network-layer error instead of allowing the request onto the wire and only rejecting after the response.

### 1.33b CORS Fail-Closed Response Validation (2026-04-21)
- `ResourceManager.cs`
- `ResourceManager.SendAsync(...)` now fail-closes `Sec-Fetch-Mode: cors` requests when origin context cannot be derived from `Origin` or `Referrer`.
- Cross-origin CORS responses are now validated post-response via `CorsHandler.IsCorsAllowed(...)` before cookies are persisted or response content is exposed.
- Responses missing valid `Access-Control-Allow-Origin` for the request origin are blocked with an explicit network-layer exception and warning log.

### 1.33c Fail-Closed Diagnostic Schema For CORS/CSP Denies (2026-05-06)
- `Logging/FailClosedDiagnostics.cs`
- `ResourceManager.cs`
- Added a centralized fail-closed diagnostic helper that emits structured deny events with mandatory fields:
  - `policy=fail-closed`
  - `decision=deny`
  - `capabilityId`
  - `stage`
  - `reasonCode`
  - `schemaVersion`
- `ResourceManager.SendAsync(...)` now emits reason-coded structured deny logs for:
  - missing CORS origin context (`CORS_ORIGIN_CONTEXT_MISSING`)
  - disallowed CORS response ACAO (`CORS_RESPONSE_DISALLOWED`)
  - blocked CORS preflight (`CORS_PREFLIGHT_BLOCKED`)
  - blocked CSP `connect-src` request (`CSP_CONNECT_SRC_BLOCKED`)
- Behavior remains fail-closed; this tranche hardens observability and promotion evidence by making deny-path semantics machine-readable.

### 1.34 CORB Enforcement Wiring Hardening (2026-03-07)
- `Security/Corb/CorbFilter.cs`
- `ResourceManager.cs`
- The existing CORB filter is now enforced from the centralized `ResourceManager` no-cors subresource paths instead of existing as broker-side logic without guaranteed runtime application.
- `FetchTextDetailedAsync(...)` now threads `Sec-Fetch-Dest` / `Sec-Fetch-Mode` metadata through text subresource requests and evaluates CORB before style/script-like cross-origin payloads are exposed to engine consumers.
- `FetchImageAsync(...)` and `FetchBytesAsync(...)` now evaluate the same CORB decision before returning cross-origin no-cors bytes, blocking HTML/XML/JSON responses masquerading as image/font/media/object payloads.
- Blocked CORB decisions now increment the shared blocked-request telemetry counter and emit explicit network warnings, making the security decision visible to diagnostics instead of failing silently.
- This is a centralized enforcement tranche, not full Milestone F closure: broader MIME/body analysis and complete cross-process CORB policy coverage still remain.

### 1.35 OOPIF Planner Assignment Enforcement (2026-03-07)
- `Security/Oopif/OopifPlanner.cs`
- `FenBrowser.FenEngine/DOM/ElementWrapper.cs`
- `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
- The existing OOPIF planner is now used as a live assignment oracle for iframe `src` navigations instead of remaining planning-only logic.
- Cross-site iframe targets that the planner classifies as requiring a new renderer process are now exposed to the engine as remote-frame assignments:
  - `contentWindow` publishes remote-frame metadata (`__fenRemoteFrame`, renderer id, origin, reason)
- `contentDocument` stays `null`
- browser frame switching resolves to an opaque/empty local search context instead of aliasing local DOM
- This is an assignment-boundary tranche, not full OOPIF completion: real cross-process frame rendering, event routing, and compositor handoff still remain open.

### 1.36 WebIDL Binding Merge-Order Hardening (2026-03-07)
- `WebIDL/WebIdlBindingGenerator.cs`
- The binding generator no longer depends on source-file ordering for partial interfaces, partial dictionaries, or partial namespaces.
- Partial definitions encountered before their non-partial base definitions are now retained and merged later instead of being silently dropped.
- Repeated mixin fragments now merge cumulatively instead of overwriting each other, and `includes` application now reuses that merged mixin state.
- Extattrs and base metadata now survive aggregation more coherently, reducing order-sensitive generated-binding drift across the IDL corpus.
- This is a generator-correctness tranche, not full WebIDL pipeline completion: broader type-resolution and generated binding parity work still remain.

### 1.37 WebIDL Type-Resolution Hardening (2026-03-07)
- `WebIDL/WebIdlBindingGenerator.cs`
- The binding generator now builds a merged type registry before emission so typedef aliases, generated enums, interface types, and dictionary init types resolve coherently during binding generation.
- `MapType(...)`, `CSharpTypeName(...)`, `ConversionExpr(...)`, and default-value handling now follow typedef chains instead of collapsing named WebIDL types into `object` prematurely.
- `EmitWrapUnwrap(...)` now emits dedicated conversion helpers for typedef aliases, enums, dictionaries, and interface-backed arguments so generated bindings preserve more of the IDL surface at compile time instead of routing everything through `any`.
- This is still a bounded tranche: callback typing, richer return-side object wrapping, and full union conversion remain incomplete.
### 1.38 Accessibility bridge attachment-lifecycle hardening (2026-03-07)
- `Accessibility/PlatformA11yBridge.cs`
- Hardened repeated accessibility attach/initialize flows so platform bridges no longer accumulate duplicate `TreeInvalidated` subscriptions when reinitialized against a new document/tree instance.
- `WindowsUiaBridge`, `LinuxAtSpiBridge`, and `MacOsNsAccessibilityBridge` now detach prior invalidation handlers before subscribing to the next tree.
- `AccessibilityManager.Attach(...)` now also detaches its previous tree invalidation relay before reattaching, and `Dispose()` removes that relay explicitly.
- This closes a real lifecycle/leak path in Milestone `F3` accessibility plumbing while the broader cross-platform platform-export surface remains incomplete.
### 1.39 POSIX sandbox working-directory and environment tightening (2026-03-07)
- `Security/Sandbox/Posix/PosixCommandSandbox.cs`
- Hardened helper-backed POSIX child launch so profiles without file capabilities no longer inherit the host working directory as a readable bind mount by default.
- Low-privilege profiles now run with `/tmp` as their sandbox working directory and do not receive `HOME`, `TMPDIR`, `TMP`, or `TEMP` environment variables unless the profile actually grants user-file or write capability.
- Writable temp-path exposure is now only granted when `FileWrite` is present, and working-directory host-path exposure is limited to profiles that explicitly need file access.
- This is another fail-closed Milestone `A4` tranche: helper-backed sandboxing remains incomplete relative to full OS-native seccomp/App Sandbox enforcement, but it no longer leaks as much host filesystem context into low-privilege child startup.
### 1.40 POSIX sandbox helper-required mode (2026-03-07)
- `Security/Sandbox/Posix/PosixOsSandboxFactory.cs`
- Added `FEN_POSIX_SANDBOX_REQUIRED=1` so Linux/macOS launches can fail closed when the required native helper (`bwrap` or `sandbox-exec`) is absent instead of silently normalizing to `NullSandbox`.
- Broker code can still opt into explicit unsandboxed overrides where supported, but the POSIX sandbox factory itself now has a production-mode path that rejects helper absence as a startup error.
- This tightens Milestone `A4` by moving more of the fail-closed behavior into the factory boundary instead of leaving all enforcement to individual process launch call sites.
### 1.41 OOPIF handoff-state hardening (2026-03-07)
- `Security/Oopif/OopifPlanner.cs`
- Extended the broker-owned OOPIF frame tree from assignment-only state into basic remote-frame handoff state.
- `FrameProxy` now carries a stable handoff token and mutable presentation-state record, and `FrameTree` now exposes:
  - `CreateHandoffTicket(...)`
  - `CommitRemoteFrame(...)`
- Cross-process child-frame assignments now retain committed URL/origin, frame sequence, surface size, and commit time in the planner-owned frame tree instead of only marking the child opaque.
- This is a real Milestone `F1` handoff-state tranche, but not full OOPIF completion: renderer boot, compositor texture routing, and remote event routing still remain open.

### 1.42 CORB MIME/body analysis hardening (2026-03-07)
- `Security/Corb/CorbFilter.cs`
- Strengthened CORB analysis so the filter no longer treats all `text/plain` responses as intrinsically sensitive, and it now blocks more realistic cross-origin document/data disguises via body sniffing.
- Added:
  - `+json` / `+xml` suffix sensitivity
  - `image/svg+xml` sensitivity
  - sniff-only handling for `text/plain`, JS MIME types, and `application/octet-stream`
  - XSSI-prefix trimming and stronger HTML/XML probes (`<!doctype`, `<html`, `<head`, `<body`, `<script`, `<iframe`, `<svg`)
- This is a stricter Milestone `F2` blocking tranche, but not full CORB completion across all body-analysis edge cases and process boundaries.

### 1.43 Accessibility platform snapshot export (2026-03-07)
- `Accessibility/PlatformAccessibilitySnapshot.cs`
- `Accessibility/AccessibilityTree.cs`
- Added a normalized platform-facing accessibility snapshot/export layer for:
  - Windows UIA
  - Linux AT-SPI
  - macOS NSAccessibility
- `AccessibilityTree.ExportPlatformSnapshot(...)` now emits flattened mapped-role nodes plus validation errors, providing a concrete platform-mapping artifact path without relying only on live bridge callbacks.
- This advances Milestone `F3` from internal-tree-only state toward end-to-end platform mapping validation, but it does not replace the still-partial runtime bridge implementations.
### 1.44 CORB and accessibility artifact commands (2026-03-07)
- `FenBrowser.Conformance/AccessibilityValidation.cs`
- `FenBrowser.Conformance/CorbValidation.cs`
- Added first-class validation helpers that turn the newer CORB and accessibility code paths into artifact-producing conformance commands instead of leaving them as internal-only subsystems.
- This strengthens Milestone `F2`/`F3` evidence retention, but does not by itself satisfy the broader end-to-end closure criteria in `Task.md`.

### 1.45 Build and Validation Recovery (2026-03-07)
- Restored `FenBrowser.Core` + `FenBrowser.FenEngine` + solution build viability after validation exposed post-tranche regressions in WebIDL generation, accessibility snapshot role mapping, CORB/resource plumbing, and POSIX sandbox environment handling.
- Validation run results:
  - `ipc-fuzz`: pass (`renderer/network/target` 45/45 each)
  - `corb-validate`: fail on same-origin JSON false-positive block
  - full milestone gate: fail (`Test262`, DOM/WPT, missing CSS/fetch artifacts)

## 1.38 CORB Same-Origin Normalization and Sensitive SVG Hardening (2026-03-07)
- `CorbFilter.IsSameOrigin(...)` now compares normalized absolute origins through `System.Uri` first, with WHATWG parsing as fallback.
- `image/svg+xml` was removed from the always-safe MIME allowlist so cross-origin opaque SVG responses remain CORB-sensitive.
- This closed the `same-origin-json` false positive in `corb-validate` while preserving the existing cross-origin HTML/JSON/SVG block cases.

## 1.39 Secure DNS Transport Socket-Family Hardening (2026-03-07)
- `HttpClientFactory.ConnectSocketAsync(...)` no longer forces every outbound connection through an IPv6 dual-mode raw socket.
- `IPEndPoint` connections now use the endpoint's own address family.
- `DnsEndPoint` connections now use `TcpClient.ConnectAsync(host, port, ct)` instead of the previous dual-mode raw socket path.
- This removes one browser-path transport divergence from the system HTTP stack and avoids the earlier access-permission failure mode in the custom Secure DNS connect path.

### 1.40 Top-Level Navigation Header Hardening (2026-03-12)
- ResourceManager.cs
- Top-level document fetches now emit navigation-class request metadata instead of reusing subresource defaults:
  - Sec-Fetch-Dest: document
  - Sec-Fetch-Mode: navigate
  - Sec-Fetch-Site: none
  - Sec-Fetch-User: ?1
  - Upgrade-Insecure-Requests: 1
- FetchTextAsync(...), FetchTextDetailedAsync(...), and FetchTextWithOptionsAsync(...) now share the same navigation-header application path, keeping top-level requests distinct from script/style/XHR-style fetches.
- Top-level document text fetches no longer force the Android/mobile UA branch that was previously shared with iframe/subresource paths; desktop navigation UA is preserved for direct browser navigations.
- Regression coverage: FenBrowser.Tests/Core/NavigationManagerRequestHeadersTests.cs.

### 1.41 Same-Site Tracking Prevention Exemption (2026-03-13)
- `FenBrowser.Core/Network/Handlers/TrackingPreventionHandler.cs`
- Enhanced Tracking Prevention now treats same-site subresources the same as same-origin ones when a request carries a page referrer, so first-party CDNs like `cdn.whatismybrowser.com` are not blocked just because they sit on a sibling subdomain.
- Same-site classification now uses a normalized registrable-site heuristic instead of raw host suffix comparison, which covers common sibling-host cases such as `www.whatismybrowser.com` -> `cdn.whatismybrowser.com` and common country-code second-level domains (`co.uk`, `com.au`, etc.) well enough for browser-path request filtering.
- Blocking still applies to known third-party tracker domains and tracking-pixel paths when the request is genuinely cross-site.
- Tracking-pixel path matching now requires a segment-style boundary after the matched token, preventing false positives such as `/logo/` tripping the old `/log` heuristic.
- ETP console diagnostics now include the blocked request path along with the host so first-party false positives are easier to reproduce from runtime logs.
- Regression coverage: `FenBrowser.Tests/Core/Network/TrackingPreventionHandlerTests.cs`.

### 1.42 Document Origin Primitives And Same-Origin Policy Helpers (2026-03-29)
- `FenBrowser.Core/Dom/V2/Document.cs`
- `FenBrowser.Core/Security/Origin.cs`
- `FenBrowser.Core/Security/SecurityChecks.cs`
- Added first-class `Document.Origin` state plus a normalized `Origin` value object for scheme/host/port tuples and opaque-origin handling.
- Added centralized same-origin, `postMessage`, SameSite-cookie, CSP, and CORS-preflight helper checks so browser-path security decisions stop being scattered across ad hoc call sites.
- Why this mattered:
  - The core DOM document model had no canonical origin state, which made future same-origin enforcement and cross-origin diagnostics brittle by construction.
  - A browser engine needs one obvious place for security boundary logic; otherwise compatibility exceptions and logging drift across features.
- Regression coverage:
  - `FenBrowser.Tests/Core/SecurityChecksTests.cs`

### 1.43 HtmlTreeBuilder Active-Formatting Reconstruction And Adoption-Agency Recovery (2026-03-29)
- `FenBrowser.Core/Parsing/HtmlTreeBuilder.cs`
- `FenBrowser.Tests/Core/Parsing/HtmlTreeBuilderFormattingRecoveryTests.cs`
- The production tree builder now reconstructs active formatting elements before character insertion and ordinary start-tag insertion, instead of only handling a narrow subset of formatting-tag paths.
- Misnested formatting end tags now route through an explicit adoption-agency implementation for the standard formatting-element set (`a`, `b`, `em`, `font`, `i`, `nobr`, `small`, `strong`, `tt`, `u`, etc.) instead of the older “pop if present” shortcut.
- Why this mattered:
  - The simplified formatting-element handling was good enough for trivial markup, but it loses structural fidelity on real-world malformed documents and is exactly the sort of parser shortcut that shows up in search-homepage and wiki-style content.
  - Reconstructing active formatting elements before ordinary insertion paths is required for the tree builder to preserve formatting continuity across the parser states the HTML spec expects.
- Regression coverage:
  - `FenBrowser.Tests/Core/Parsing/HtmlTreeBuilderTableCellFormattingTests.cs`
  - `FenBrowser.Tests/Core/Parsing/HtmlTreeBuilderFormattingRecoveryTests.cs`

### 1.44 First-Class Logging, Security Policy, And Linux AT-SPI Event Brokerage (2026-03-29)
- `FenBrowser.Core/Logging/LogCategory.cs`
- `FenBrowser.Core/Logging/LogContext.cs`
- `FenBrowser.Core/Logging/LogEntry.cs`
- `FenBrowser.Core/Logging/LogManager.cs`
- `FenBrowser.Core/FenLogger.cs`
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/Security/SecurityDecision.cs`
- `FenBrowser.Core/Security/BrowserSecurityPolicy.cs`
- `FenBrowser.Core/Security/Sandbox/SandboxLaunchPolicy.cs`
- `FenBrowser.Core/NetworkService.cs`
- `FenBrowser.Core/Accessibility/LinuxAtSpiEventBroker.cs`
- `FenBrowser.Core/Accessibility/PlatformA11yBridge.cs`
- Added ambient correlation/component/data scopes for production logging so cross-process incidents can be tied together without hand-built log prefixes.
- `LogManager` now supports JSON log mirroring, bounded archive retention, caller/component metadata capture, and stable categories for `Security`, `Accessibility`, `ProcessIsolation`, and `DevTools`.
- `BrowserSecurityPolicy` is now the canonical policy gate for outbound network requests, top-level navigation acceptance, and remote-debug bind decisions.
- `SandboxLaunchPolicy` prevents `NullSandbox` from silently presenting as production enforcement; unsandboxed child launch now requires explicit operator override rather than error-path drift.
- `NetworkService` now rejects malformed or policy-disallowed absolute URIs before dispatch and logs those denials through the structured security path.
- Linux accessibility now uses a bounded AT-SPI event broker with DBus signal emission and observable queue-drop/error reporting, replacing the earlier partial stub posture.
- Verification:
  - `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -nologo` completed successfully on `2026-03-29`.

## 6.7 P1 DOM, Engine, And WebIDL Contract Hardening (2026-03-29)

- `FenBrowser.Core/Dom/V2/TreeScope.cs`
- `FenBrowser.Core/Dom/V2/Node.cs`
- `FenBrowser.Core/Dom/V2/ContainerNode.cs`
- `FenBrowser.Core/Dom/V2/Document.cs`
- `FenBrowser.Core/Dom/V2/Element.cs`
- `FenBrowser.Core/Dom/V2/ShadowRoot.cs`
- `FenBrowser.Core/Dom/V2/TreeWalker.cs`
- `FenBrowser.Core/Dom/V2/NodeIterator.cs`
- `FenBrowser.Core/Dom/V2/Security/AttributeSanitizer.cs`
- `FenBrowser.Core/DomSerializer.cs`
  - DOM support surfaces were tightened around real browser invariants instead of permissive helper behavior.
  - Tree-scope ownership now invalidates and rebuilds ID indexes correctly across subtree removal and reparenting.
  - `Document.GetElementById(...)` now defers to the active `TreeScope`, which keeps lookup ownership centralized.
  - `TreeWalker.CurrentNode` now enforces root-boundary membership instead of allowing drift outside the traversal root.
  - `NodeIterator` now tracks subtree removal and re-anchors reference state rather than silently keeping stale traversal anchors.
  - `ShadowRoot.ActiveElement` now requires membership in the current shadow tree, and `adoptedStyleSheets` rejects null entries and deduplicates repeated sheets.
  - `AttributeSanitizer` now logs high-signal sanitization decisions through structured logging.
  - `DomSerializer` now preserves doctype emission and non-pretty text fidelity, which matters for protocol markup editing and automation source capture.

- `FenBrowser.Core/Engine/EngineContext.cs`
- `FenBrowser.Core/Engine/EngineInvariants.cs`
- `FenBrowser.Core/Engine/NavigationSubresourceTracker.cs`
- `FenBrowser.Core/Engine/PipelineStage.cs`
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/IBrowserEngine.cs`
- `FenBrowser.Core/INetworkService.cs`
- `FenBrowser.Core/NetworkConfiguration.cs`
- `FenBrowser.Core/NetworkService.cs`
- `FenBrowser.Core/UiThreadHelper.cs`
  - Engine lifecycle rules are now explicit and shared from one transition matrix instead of duplicated across separate invariant helpers.
  - `PipelineStage` now exposes ordered transition validation plus stable input/output stage metadata for instrumentation and diagnostics.
  - `NavigationSubresourceTracker` now exposes immutable pending-load snapshots for navigation-scoped completion logic and tests.
  - `BrowserSettings` now normalizes and validates user-facing URLs, zoom bounds, font presets, bookmark/startup lists, and log settings before persistence.
  - `IBrowserEngine` and `INetworkService` now carry URI-based async overloads, cancellation, and explicit load-state contracts rather than string-only permissive APIs.
  - `NetworkConfiguration` and `NetworkService` now fail fast on invalid configuration and route document/resource fetches through the centralized security policy before issuing requests.
  - `UiThreadHelper` now uses an explicit dispatcher-adapter contract instead of silent compatibility shims.

- `FenBrowser.Core/WebIDL/WebIdlBindingGenerator.cs`
- `FenBrowser.WebIdlGen/Program.cs`
  - WebIDL generation is now deterministic by definition order, output ordering, and manifest hashing.
  - The generator tool now writes `webidl-bindings-manifest.json`, removes stale generated outputs, supports `--verify`, and reports parse errors with file-relative attribution.
  - This hardens the binding pipeline from "generated breadth exists" toward "generated ownership is reproducible and auditable".

### 1.45 Workspace-Root Diagnostics Defaults And Structured Logger Path Unification (2026-03-30)
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/Logging/DiagnosticPaths.cs`
- `FenBrowser.Core/Logging/LogManager.cs`
- `FenBrowser.Core/Logging/StructuredLogger.cs`
- `BrowserSettings.LogSettings` now defaults logging on for real browser runs and normalizes the legacy `AppContext.BaseDirectory/logs` default back to workspace-root `logs`, so clean-state debug cycles stop drifting into host-bin folders.
- `LogManager.InitializeFromSettings()` now initializes `StructuredLogger` with the resolved active log path, which makes raw-source dumps, engine snapshots, rendered-text snapshots, and module log files share one authoritative diagnostics root.
- `StructuredLogger` now emits the runtime fetch artifact as `raw_source_*.html`, matching the verification contract and avoiding the older `network_fetch_*.html` naming drift on the live host path.
- 2026-04-18 follow-up: `DiagnosticPaths` now maps both legacy "root artifact" callers and log-artifact callers into workspace `logs/`, so `.txt`, `.png`, `.png.meta`, `.html`, and captured `.js` diagnostics no longer spill into the repository root.
- Why this mattered:
  - the old split-path behavior produced thin or misleading runtime evidence because some artifacts landed under repo-root `logs` while others still resolved relative to `AppContext.BaseDirectory`.
  - production logging cannot be first-class if the artifact root itself is unstable.
- Verification:
  - `dotnet build FenBrowser.sln -nologo`: pass on `2026-03-30`.
  - required host cycle on `2026-03-30` emitted unified workspace-root diagnostics including:
    - `logs/raw_source_20260330_003122.html`
    - `logs/engine_source_20260330_003123.html`
    - `logs/rendered_text_20260330_003123.txt`
    - `logs/fenbrowser_20260330_003121.log`
    - `logs/fenbrowser_20260330_003121.jsonl`

### 1.46 Thin Contract Hardening For Certificate, Cache, And Geometry Primitives (2026-03-30)
- `FenBrowser.Core/CertificateInfo.cs`
  - Certificate identity fields now normalize whitespace aggressively, thumbprints normalize to uppercase no-separator form, and SAN entries are trimmed, deduplicated, and stored as a stable read-only snapshot.
  - Added explicit `HasPolicyErrors`, `IsDateRangeValid`, `IsCurrentlyValid`, and invalid-date-range handling in `ExpiryStatus` so security/UI call sites stop inferring trust state from partially normalized raw fields.
- `FenBrowser.Core/Cache/CacheKey.cs`
  - Cache keys now trim inputs, default blank partition keys to `default`, expose `IsEmpty`, and use stable ordinal equality/hash semantics.
  - This closes ambiguity around cross-site partition ownership for callers that were previously allowed to pass whitespace-shaped partitions.
- `FenBrowser.Core/Cache/ShardedCache.cs`
  - The LRU cache remains intentionally small, but it now exposes explicit `Capacity`, `HitCount`, `MissCount`, `EvictionCount`, `Contains(...)`, and `TryRemove(...)`.
  - Evictions and lookup outcomes are now observable instead of being hidden inside a private utility path, which matters for production cache diagnostics and policy debugging.
- `FenBrowser.Core/Math/CornerRadius.cs`
- `FenBrowser.Core/Math/Thickness.cs`
  - Geometry/value primitives now expose `Empty`, zero-state and negative-state helpers, plus `ClampNonNegative()` so layout/paint code can enforce final value invariants without spreading ad hoc clamping logic across hot paths.
  - `CornerRadius` now also carries full equality/hash/operator semantics consistent with its value-type role.
- Why this mattered:
  - P2 is not about making thin files bigger; it is about making them final, explicit, and safe to depend on across the browser.
  - Cache keys, certificate summaries, and geometry primitives sit on frequently reused call paths, so ambiguity here becomes systemic drift quickly.
- Verification:
  - `FenBrowser.Tests/Core/ThinContractTests.cs`
  - `FenBrowser.Tests/Core/ShardedCacheTests.cs`
  - focused test run on `2026-03-30`: pass (`8/8`).

### 1.47 Console Logger And CSS Radius Value-Contract Hardening (2026-03-30)
- `FenBrowser.Core/ConsoleLogger.cs`
  - Console logging now serializes writes through a single synchronization gate so color state and line output stop racing each other when multiple callers log concurrently.
  - Messages are normalized before emission, timestamps are explicit, and `LogError(...)` now records exception type/message consistently instead of only concatenating a raw message fragment.
- `FenBrowser.Core/Css/CssCornerRadius.cs`
  - `CssLength` and `CssCornerRadius` remain intentionally small, but their value semantics are now explicit:
    - stable equality/hash/operators
    - `Zero` / `Empty`
    - zero/negative/percent-state helpers
    - non-negative clamping
  - This turns the radius value types into deliberate contracts instead of anonymous field bags.
- Why this mattered:
  - Logging and CSS values both sit on hot or high-fanout paths. Thin files in these areas need final semantics, not permissive convenience behavior.
  - A production renderer should not depend on every call site open-coding message normalization or radius-state checks.
- Verification:
  - `FenBrowser.Tests/Core/ThinContractTests.cs`
  - focused regression slice on `2026-03-30` included console logger and CSS radius contract coverage.

### 1.48 Diagnostics Routing And Verification Signal Hardening (2026-03-30)
- `FenBrowser.Core/Logging/DiagnosticPaths.cs`
- `FenBrowser.Core/Logging/LogManager.cs`
- `FenBrowser.Core/Logging/StructuredLogger.cs`
  - Diagnostics routing now treats `FEN_DIAGNOSTICS_DIR` as an authoritative workspace-root override for both the main structured log stream and the raw/engine/rendered artifact dumps.
  - This closes the earlier split where `StructuredLogger` and `LogManager` could still fall back to the host execution directory even when the operator explicitly requested a workspace-root diagnostics run.
- `FenBrowser.Core/Verification/ContentVerifier.cs`
  - The verification summary now records `Text Density` (`chars/node`) and classifies low text-to-source ratios into:
    - normal healthy output,
    - tolerated low-ratio output for script-heavy but otherwise corroborated pages,
    - suspicious low-ratio output when render evidence is weak.
  - The net effect is that verification stops emitting a false parser-failure warning when the screenshot, DOM size, and matched-rule evidence already prove the page is alive.
- Why this mattered:
  - First-class diagnostics require one artifact root and truthful severity. A production browser cannot claim incident-grade logging if the path contract drifts by host working directory or if a healthy page is mislabeled as a likely parser failure.
  - P2 closure needed the logging/verification surface itself to become final, not just the surrounding value objects.
- Verification:
  - `FenBrowser.Tests/Core/P2ClosureContractTests.cs`
    - added direct coverage for `DiagnosticPaths`, `StructuredLogger`, and `ContentVerifier` health classification.
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -nologo --no-build --no-restore --filter "FullyQualifiedName~P2ClosureContractTests"`: pass on `2026-03-30`.
  - required host runtime check on `2026-03-30` emitted the full diagnostics set under workspace-root `logs`.

### 1.49 Render/Perf P2 Core Budget Configuration And Bounded Cache Primitives (2026-03-30)
- `FenBrowser.Core/RenderPerformanceConfiguration.cs`
- `FenBrowser.Core/Cache/BoundedLruCache.cs`
  - Added a centralized render/perf configuration object for hot-path cache and scheduler budgets:
    - typography cache entry and byte limits
    - reserved render budget for normal and busy frames
    - per-frame event-loop task caps
  - Added a reusable bounded LRU cache primitive with:
    - entry-count limits
    - byte-budget limits
    - hit/miss/eviction observability
    - stable snapshot export for diagnostics and tests
- Why this mattered:
  - render/perf P2 could not close while memory discipline lived as scattered magic numbers inside engine classes.
  - the browser needed one explicit place to define render-side budget policy and one explicit primitive to enforce bounded cache ownership.
- Verification:
  - `FenBrowser.Tests/Rendering/TypographyCachingTests.cs`
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -nologo --no-build --filter "FullyQualifiedName~TypographyCachingTests"`: pass on `2026-03-30`.

### 1.50 Browser Surface Request-Header Unification And Client-Hint Consistency (2026-04-04)
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/BrowserSurfaceProfile.cs`
- `FenBrowser.Core/ResourceManager.cs`
- `FenBrowser.Core/NetworkService.cs`
- `FenBrowser.Tests/Core/BrowserSettingsTests.cs`
- `FenBrowser.Tests/Core/NavigationManagerRequestHeadersTests.cs`
  - Added `BrowserSettings.ApplyBrowserRequestHeaders(...)` as the canonical browser identity helper for outbound requests that should present a first-class browser surface.
  - `ResourceManager` and `NetworkService` now apply the same browser request surface instead of mixing ad hoc `User-Agent` assignment with partial client-hint emission.
  - Chromium-family browser profiles now separate low-entropy brands from full-version brands:
    - `Sec-CH-UA` carries the major-version browser brands plus a standards-shaped GREASE brand.
    - `Sec-CH-UA-Full-Version-List` carries the full browser and Chromium versions instead of repeating the low-entropy values.
    - `Sec-CH-UA-Full-Version` now resolves to the actual browser brand full version rather than the GREASE placeholder version.
- Why this mattered:
  - the old request surface could advertise `Edg/146...` in `User-Agent` while still leaking `99` through the full-version client-hint path, which produced contradictory browser-version detection on live sites.
  - browser identity needs one source of truth across navigation, subresource fetches, and compatibility APIs.
- Verification:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~NavigationManagerRequestHeadersTests|FullyQualifiedName~BrowserSettingsTests"`: pass on `2026-04-04`.
  - live host navigation to `https://httpbin.org/headers` on `2026-04-04` confirmed:
    - `Sec-CH-UA: " Not;A Brand";v="99", "Chromium";v="146", "Microsoft Edge";v="146"`
    - `Sec-CH-UA-Full-Version: "146.0.7800.12"`
    - `Sec-CH-UA-Full-Version-List: " Not;A Brand";v="99.0.0.0", "Chromium";v="146.0.7800.12", "Microsoft Edge";v="146.0.7800.12"`

### 1.50.1 FenBrowser Request Identity And Client-Hint Privacy Split (2026-06-26)
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/BrowserSurfaceProfile.cs`
- `FenBrowser.Tests/Core/BrowserSettingsTests.cs`
- `FenBrowser.Tests/Core/NavigationManagerRequestHeadersTests.cs`
  - The default FenBrowser profile keeps a Chromium-compatible `User-Agent` grammar and appends `FenBrowser/1.0` after the Safari token instead of inserting it before `AppleWebKit`.
  - FenBrowser client hints now separate the browser brand version from the embedded Chromium engine version:
    - low-entropy brands expose `"FenBrowser";v="1"` and `"Chromium";v="146"`.
    - full-version brands expose `"FenBrowser";v="1.0.0.0"` and `"Chromium";v="146.0.7800.12"`.
  - Default navigation sends only low-entropy client hints; `ImproveBrowser=true` opts into high-entropy values such as full version, platform version, architecture, bitness, and model.
- Verification:
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~NavigationManagerRequestHeadersTests|FullyQualifiedName~BrowserSettingsTests"`: pass on `2026-06-26`.

### 1.51 Rendered-Text Artifact Normalization (2026-04-04)
- `FenBrowser.Core/Logging/StructuredLogger.cs`
  - Rendered-text artifact export now normalizes a small set of recurring mojibake sequences before the text snapshot is written to disk.
- Why this mattered:
  - diagnostics should not report fake text regressions purely because a known symbol sequence was serialized through an inconsistent encoding path.
  - this is an artifact-quality hardening step for operator evidence, not a replacement for upstream text shaping or entity-decoding fixes.

### 1.52 Browser Surface Media Evaluation And Shared Cookie-Jar Routing (2026-04-04)
- `FenBrowser.Core/Storage/BrowserCookieJar.cs`
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/BrowserSurfaceProfile.cs`
- `FenBrowser.Core/ResourceManager.cs`
- `FenBrowser.Tests/Core/BrowserCookieJarTests.cs`
- `FenBrowser.Tests/Core/BrowserSettingsTests.cs`
  - Added `BrowserCookieJar` as the shared browser-level cookie authority for both network traffic and `document.cookie` bridges.
  - The jar enforces production cookie semantics instead of ad hoc dictionaries:
    - `Secure`
    - `HttpOnly`
    - `SameSite`
    - `Partitioned`
    - `__Secure-` / `__Host-` prefixes
    - third-party read/write blocking through the same policy path used by the browser
  - `ResourceManager` now attaches request cookies and stores response cookies across text, image, byte, and generic send paths, which closes the earlier split where third-party script detection could set cookies without later replaying them.
  - `BrowserSettings.GetBrowserSurface(...)` now derives `preferred-color-scheme` from the actual host theme setting, and `BrowserSurfaceProfile.MatchesMediaQuery(...)` now evaluates real browser-facing conditions instead of the earlier string-fragment approximation.
  - Media-query evaluation now supports:
    - comma-separated OR groups
    - `and` / `not` / `only`
    - media types
    - width / height constraints
    - orientation
    - `prefers-color-scheme`
    - `prefers-reduced-motion`
    - `pointer`
    - `hover`
- Why this mattered:
  - `whatismybrowser.com` exposed two production gaps at once:
    - browser-surface media queries were too weak to drive correct style parity
    - cookie state was split between network and DOM paths, so third-party cookie detection could not behave like a real browser
  - A browser cannot claim compatibility parity while request cookies, `document.cookie`, and media features disagree about the active page state.
- Verification:
  - `dotnet build FenBrowser.Host/FenBrowser.Host.csproj -c Debug --no-restore`: pass on `2026-04-04`.
  - `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~BrowserSettingsTests|FullyQualifiedName~BrowserCookieJarTests|FullyQualifiedName~NavigationManagerRequestHeadersTests|FullyQualifiedName~JavaScriptEngineLifecycleTests.MatchMedia_TracksThemeAndViewportSurfaceChanges" --no-restore`: pass (`9/9`) on `2026-04-04`.

## 6.41 Logging I/O Resilience Hardening (2026-04-18)

- `FenBrowser.Core/Logging/ResilientFileWriter.cs` (new)
  - Added bounded retry + transient-share-failure handling for append/write/move operations used by diagnostics and structured logs.
  - Failures remain best-effort and non-throwing to protect runtime hot paths.
- `FenBrowser.Core/Logging/DiagnosticPaths.cs`
  - `AppendRootText(...)` and `AppendLogText(...)` now route through resilient append behavior instead of direct `File.AppendAllText(...)`.
- `FenBrowser.Core/Logging/LogManager.cs`
  - Initialization/header writes, sink appends, and archive rotations now use resilient file operations.
  - Logging still preserves the no-throw contract during sink failures.
- `FenBrowser.Core/Logging/StructuredLogger.cs`
  - Module log appends and dump artifact writes now use resilient write paths.
- Rationale:
  - Long-lived page runs generate high-frequency diagnostics; resilient sink behavior prevents transient file-sharing errors from cascading into exception storms or noisy debugger interruptions.

### 1.53 Engine Logging Runtime Cutover (2026-04-20)
- `FenBrowser.Core/Logging/EngineLogContracts.cs` (new)
- `FenBrowser.Core/Logging/EngineLogDispatcher.cs` (new)
- `FenBrowser.Core/Logging/EngineLogSinks.cs` (new)
- `FenBrowser.Core/Logging/EngineLogger.cs` (new)
- `FenBrowser.Core/Logging/EngineLog.cs` (new)
- `FenBrowser.Core/Logging/LogManager.cs`
- `FenBrowser.Core/FenLogger.cs`
- `FenBrowser.Core/Logging/EngineCapabilities.cs`
  - Added the new structured engine logging runtime with explicit `LogSubsystem`, `LogSeverity`, and `LogMarker` contracts.
  - Added non-blocking dispatch, console + NDJSON sinks, and in-memory ring retention under workspace-root `logs`.
  - `LogManager` and `FenLogger` now route through `EngineLog` as compatibility facades so existing call sites stay operational while using the new runtime.
  - Feature-gap reporting now maps to marker semantics (`Unimplemented`, `Partial`) instead of free-form-only warning strings.
- Verification:
  - `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Debug -v minimal`: pass on `2026-04-20`.

### 1.54 Engine Logging Runtime Completion Surfaces (2026-04-20)
- `FenBrowser.Core/Logging/EngineLogContracts.cs`
- `FenBrowser.Core/Logging/EngineLogSinks.cs`
- `FenBrowser.Core/Logging/EngineLog.cs`
- `FenBrowser.Core/Logging/EngineFailureBundleExporter.cs` (new)
- `FenBrowser.Core/FenLogger.cs`
- `FenBrowser.Core/Logging/LogManager.cs`
  - Added runtime deduplication primitives (`once per document`, `once per session`, `rate-limited`) with suppression summaries emitted through `EngineLog`.
  - Added trace-event sink output (`fenbrowser_trace_*.jsonl`) alongside existing NDJSON logs for timeline-style diagnostics.
  - Added failure-bundle export surface writing under workspace-root `Results/<timestamp>/` with `summary.json` + `logs.ndjson` (+ best-effort screenshot/trace copy).
  - Runtime initialization now enables trace sink by default for `FenLogger`/`LogManager` compatibility bootstraps.

### 1.55 EngineLog External Event Publishing (2026-04-20)
- `FenBrowser.Core/Logging/EngineLogger.cs`
- `FenBrowser.Core/Logging/EngineLog.cs`
  - Added raw structured-event dispatch path (`WriteRaw(...)`) for pre-serialized events.
  - Added `EngineLog.EngineEventWritten` and `EngineLog.PublishExternalEvent(...)` for cross-process log aggregation and live DevTools streaming.
  - External events now share the same sink + compatibility-buffer pipeline as in-process events.

### 1.56 EngineLog Per-Document Counters (2026-04-20)
- `FenBrowser.Core/Logging/EngineLogContracts.cs`
- `FenBrowser.Core/Logging/EngineLog.cs`
  - Added per-document log counters keyed by `documentId`/`navigationId`/`url` with severity buckets and last-seen context metadata (`tabId`, `frameId`, `url`, sequence, timestamp).
  - Added `EngineLog.GetPerDocumentCounters(...)` for runtime and DevTools diagnostics queries.
  - `EngineLog.ClearCompatibilityBuffer()` now also clears per-document counters to preserve deterministic test/reset semantics.

### 1.57 Engine Logging Preset Profiles (2026-04-20)
- `FenBrowser.Core/Logging/EngineLoggingPresets.cs` (new)
- `FenBrowser.Core/Logging/EngineLog.cs`
- `FenBrowser.Core/EngineLogCompat.cs`
- `FenBrowser.Core/BrowserSettings.cs`
  - Added named logging presets:
    - `developer`
    - `testrun`
    - `perf`
    - `ci`
  - Presets now configure severity floors, subsystem overrides, and sink defaults in a consistent profile surface.
  - Preset source order:
    - `FEN_LOG_PRESET` environment override
    - persisted `BrowserSettings.Logging.LoggingPreset`
  - Added runtime `EngineLog.ApplyPreset(...)` for CLI/runner-level profile selection.

### 1.58 Parser + Network Resilience Policy Surface (2026-04-20)
- `FenBrowser.Core/BrowserSettings.cs`
- `FenBrowser.Core/Parsing/HtmlResilienceContracts.cs` (new)
- `FenBrowser.Core/Parsing/HtmlTokenizer.cs`
- `FenBrowser.Core/Parsing/HtmlTreeBuilder.cs`
- `FenBrowser.Core/ResourceManager.cs`
  - Added `BrowserSettings.Resilience` as the canonical limit/timing policy for parser and network safeguards.
  - Added parser outcome contracts (`HtmlParsingOutcomeClass`, `HtmlParsingReasonCode`, `HtmlParsingOutcome`) and wired tokenizer/tree-builder outcome reporting (`InputSizeLimitExceeded`, `TokenEmissionLimitExceeded`, depth-limit degradation, exception failure).
  - Resource fetch detailed results now include structured failure metadata (`FailureReason`, `LimitType`, `InputSizeBytes`, `IsRetryable`, `DurationMs`) and explicit limit statuses (`FetchStatus.LimitExceeded`).
  - Added deterministic network guardrails:
    - redirect-hop ceiling from policy
    - request/navigation timeout from policy
    - text-body and binary/image body-size ceilings with explicit fail-closed outcomes/logging
    - default text-resource body ceiling is 16 MiB, with normalization migrating the old 8 MiB default, so large modern script bundles can load while still reporting `FetchStatus.LimitExceeded` above the configured cap
  - This keeps malformed or high-cost payloads bounded while preserving deterministic failure classification for runtime diagnostics.

### 1.59 Strict-Mode `srcdoc` Sanitization Control (2026-05-01)
- `FenBrowser.Core/Dom/V2/Security/AttributeSanitizer.cs`
  - `srcdoc` sanitization is opt-in under strict mode via `AttributeSanitizer.BlockSrcdocInStrictMode`; default behavior preserves `srcdoc` values for standards coverage and compatibility.

### 1.60 Site-Per-Process-Lite Assignment Policy (2026-05-08)
- `FenBrowser.Core/ProcessIsolation/RendererIsolationPolicies.cs`
  - Added assignment policy mode surface for renderer process mapping:
    - `OriginStrict` (existing behavior)
    - `SitePerProcessLite` (new).
  - Added `SiteIsolationPolicy` for top-level site assignment-key derivation (scheme + registrable-host approximation).
  - `RendererTabIsolationRegistry` now accepts an explicit assignment-policy mode and derives reassignment decisions through the selected policy instead of hard-coding origin-only mapping.

- Net effect:
  - Core process-isolation policy now supports production migration from strict per-origin reassignment to site-per-process-lite behavior while preserving deterministic fail-closed assignment for opaque and local schemes.

### 1.61 Cookie Ingress/Egress Diagnostics Toggle (2026-05-18)
- `FenBrowser.Core/Storage/CookieDiagnostics.cs` (new)
- `FenBrowser.Core/Storage/BrowserCookieJar.cs`
- `FenBrowser.Core/BrowserSettings.cs`
  - Added opt-in structured cookie diagnostics for accepted/rejected `Set-Cookie` ingress and outbound `Cookie` header egress.
  - Added `BrowserSettings.Logging.LogCookies` and env override `FEN_LOG_COOKIES=1`.
  - Diagnostics are PII-safe by construction: no plaintext cookie values, only cookie name, value length, and short hash tag for correlation.
  - Diagnostics are fail-safe: all logging paths swallow exceptions and cannot break cookie/network flow.

### 1.62 Lazy HTML Token-Pool Slot Initialization (2026-07-14)

- `FenBrowser.Core/Parsing/HtmlTokenPool.cs`
  - The fixed-capacity per-parser token pool no longer constructs all 25,664 token objects in its constructor. Start-tag, end-tag, character, comment, and doctype slots are initialized on first rent and then retain the existing reset-and-reuse behavior.
  - Capacity and ring-wrap behavior are unchanged, so memory remains bounded and tokenizer/tree-builder ordering semantics are preserved. The arrays still define the ownership boundary; no process-global or unbounded pool was introduced.
  - `TotalAllocated` now reports real lazy slot creation, and `ReuseRatio` reports actual reused rents instead of claiming 100% reuse before any token was reused.
- `FenBrowser.Tests/Core/Parsing/HtmlTokenPoolTests.cs`
  - Covers zero token-object construction before first rent, state reset, reference reuse after `ResetAll`, and allocation/reuse telemetry.

Five-process Release benchmark medians:

| Fixture | Parse before | Parse after | Parser allocations before | Parser allocations after |
| --- | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 35.60 ms | 34.01 ms (-4.47%) | 3,044,792 B | 1,310,976 B (-56.94%) |
| steady-state-damage-animation | 2.20 ms | 1.73 ms (-21.36%) | 2,609,680 B | 838,696 B (-67.86%) |
| dense-text-flow | 2.26 ms | 1.01 ms (-55.31%) | 2,387,968 B | 605,544 B (-74.64%) |
| wrapped-multiline-text | 7.02 ms | 0.52 ms (-92.59%) | 2,193,928 B | 394,080 B (-82.04%) |

Verification:

- `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FenBrowser.Tests.Core.Parsing&FullyQualifiedName!~HtmlParserTraceTests" -v quiet /nodeReuse:false`: pass (`67/67`).
- `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~HtmlTokenPoolTests|FullyQualifiedName~HtmlParserTraceTests.ParseDocumentDetailed_WritesHtmlParsingTraceEvents" -v quiet /nodeReuse:false`: pass (`3/3`) when the file-trace contract is isolated from parallel diagnostics writers.
- The default parallel parsing slice passed `67/68`; its trace-sink test is the same parallel file-diagnostics interference observed at the baseline (`65/66`) and passes in the serial slice.
- A post-change sampled EventPipe trace reduced `HtmlTokenPool..ctor()` inclusive samples from `5.841` to `1.908` units (`-67.3%`), confirming that constructor work was removed rather than shifted into benchmark accounting.

### 1.63 Allocation-Free Empty Event-Listener Snapshots (2026-07-14)

- `FenBrowser.Core/Dom/V2/EventTarget.cs`
  - Dispatch previously requested a concrete copied list at every target and phase. Targets without a listener for the event type returned a newly allocated empty `List<EventListenerEntry>`, so an eight-node bubbling path created fifteen empty lists per dispatch before invoking zero callbacks.
  - Event storage now offers an internal `TryGetEventListeners` snapshot operation. It returns no value for a missing event type and still creates the same concrete list copy when listeners exist, preserving safe listener mutation during dispatch, once-listener removal, passive state, and capture/bubble semantics.
  - The existing non-null `GetEventListeners` contract remains available to other internal diagnostics/tests. Dispatch uses the allocation-free missing-listener result and indexed concrete-list iteration.
- `FenBrowser.Tests/Core/EventTargetDispatchTests.cs`
  - Adds included coverage for listener-free state cleanup, capture/target/bubble ordering, once and passive options, and removal during dispatch. These contracts previously existed only under directories excluded by the active test project.
- `FenBrowser.Tests/Performance/DomPerformanceBenchmarkRunnerTests.cs`
  - Adds broad allocation ceilings that detect reintroduction of per-phase empty lists without relying on narrow wall-clock CI thresholds.

Five-process Release medians compare baseline reports `112846`-`112848` with retained reports `113203`-`113205`:

| Workload | Time before | Time after | Allocation before | Allocation after |
| --- | ---: | ---: | ---: | ---: |
| no listeners | 19.584 ms | 16.368 ms (-16.42%) | 20,800,000 B | 11,200,000 B (-46.15%) |
| capture/target/bubble listeners | 28.182 ms | 26.609 ms (-5.58%) | 24,000,000 B | 17,600,000 B (-26.67%) |

An initial `IReadOnlyList` return shape reduced empty allocations but regressed the listener-bearing median to 43.791 ms because interface enumeration boxed/virtualized the hot loop. That experiment was rejected. The retained concrete `TryGet` snapshot keeps both allocation and time improvements.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `EventTargetDispatchTests` and `DomPerformanceBenchmarkRunnerTests`: pass (`6/6`).
- All retained benchmark runs observed zero callbacks in the no-listener case and exactly 60,000 callbacks in the listener-bearing case.

### 1.64 Lazy DevTools Child-Mutation Payloads (2026-07-14)

- `FenBrowser.Core/Dom/V2/Node.cs`
- `FenBrowser.Core/Dom/V2/ContainerNode.cs`
  - Child insertion and removal previously allocated a `List<Node>` and backing array for the static DevTools mutation event even when no instrumentation subscriber was installed.
  - Child-list notification now snapshots the static handler first and constructs the observable added/removed payload only when a subscriber exists. MutationObserver record creation and ancestor propagation are unchanged.
  - The handler snapshot preserves one coherent subscriber set for the notification while avoiding formatted diagnostics or new process-global caches in the mutation path.
- `FenBrowser.Tests/Core/DomMutationNotificationTests.cs`
  - Adds included coverage that subscribed DevTools consumers still receive ordered insert/remove records with the original payload shape.
- `FenBrowser.Tests/Performance/DomPerformanceBenchmarkRunnerTests.cs`
  - Adds a broad append/remove allocation ceiling that detects eager child-list payload allocation without depending on wall-clock timing.

Five-process Release medians compare the original DOM baseline with retained reports `113655`-`113659`:

| Workload | Time before | Time after | Allocation before | Allocation after | Gen0 before | Gen0 after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 append/remove cycles | 4.754 ms | 3.888 ms (-18.22%) | 5,584,016 B | 3,824,016 B (-31.52%) | 2 | 1 |

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `DomMutationNotificationTests` and `DomPerformanceBenchmarkRunnerTests`: focused Release slice passes.
- All retained runs completed 20,000 mutations with the child detached at completion; the subscribed notification contract is checked independently so the allocation benchmark remains representative of the disabled-instrumentation path.

### 1.65 Demand-Driven Child Mutation Records (2026-07-14)

- `FenBrowser.Core/Dom/V2/ContainerNode.cs`
  - Child insertion and removal previously created a `MutationRecord` plus an added/removed node array before checking whether the target or any ancestor had a registered `MutationObserver` list.
  - The direct observer is now checked first, the DevTools notification retains its existing position, and ancestor traversal creates the shared record only when it encounters a registered observer list. This preserves direct-before-DevTools-before-subtree delivery order while making the common unobserved mutation path record-free.
  - Attribute and character-data records are unchanged. A node whose observer list remains allocated after disconnect may still take the conservative record-building path; this preserves correctness and keeps the change local rather than adding global observer accounting.
- `FenBrowser.Tests/Core/DomMutationNotificationTests.cs`
  - Covers direct insertion, direct removal, ancestor-subtree insertion, and the independent DevTools payload contract.
- `FenBrowser.Tests/Performance/DomPerformanceBenchmarkRunnerTests.cs`
  - Tightens the broad append/remove allocation ceiling to detect eager MutationRecord or node-array construction.

Five-process Release medians compare retained reports `113655`-`113659` with `113956`-`114000`:

| Workload | Time before | Time after | Allocation before | Allocation after | Gen0 before | Gen0 after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 append/remove cycles | 3.888 ms | 3.245 ms (-16.54%) | 3,824,016 B | 1,424,016 B (-62.76%) | 1 | 0 |

Relative to the original DOM baseline, the two retained child-mutation units reduce allocation by 74.50% (`5,584,016 B` to `1,424,016 B`) and median time by 31.74% (`4.754 ms` to `3.245 ms`).

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `DomMutationNotificationTests` and `DomPerformanceBenchmarkRunnerTests`: focused Release slice passes.
- Retained benchmark runs complete 20,000 mutations with zero Gen0/1/2 collections.

### 1.66 Allocation-Free Ancestor Class Iteration (2026-07-14)

- `FenBrowser.Core/Dom/V2/Element.cs`
  - Ancestor bloom-filter refresh previously enumerated the cached `DOMTokenList` through its `yield` iterator on every element attachment. Even an empty, already parsed class list allocated one iterator state object per `ComputeFeatureHash` call.
  - The hot path now snapshots the token count and uses indexed access over the existing cached token array. Tag, ID, class, namespace, casing, bloom-bit, and descendant-propagation semantics are unchanged.
- `FenBrowser.Tests/Core/AncestorFilterTests.cs`
  - Adds included coverage for tag/ID/multi-class propagation, descendant selector behavior, and filter refresh when a subtree moves between parents. Equivalent historical tests under the excluded `Engine` directory did not participate in the active test project.
- `FenBrowser.Tests/Performance/DomPerformanceBenchmarkRunnerTests.cs`
  - Tightens the append/remove allocation ceiling to detect reintroduction of an iterator allocation per attachment.

Five-process Release medians compare retained reports `113956`-`114000` with `114304`-`114308`:

| Workload | Time before | Time after | Allocation before | Allocation after | Allocation removed |
| --- | ---: | ---: | ---: | ---: | ---: |
| 10,000 append/remove cycles | 3.245 ms | 2.599 ms (-19.91%) | 1,424,016 B | 944,016 B (-33.71%) | 480,000 B (48 B/attachment) |

Relative to the original DOM baseline, the three retained mutation/ancestor-filter units reduce allocation by 83.09% (`5,584,016 B` to `944,016 B`) and median time by 45.33% (`4.754 ms` to `2.599 ms`), with zero Gen0/1/2 collections after optimization.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `AncestorFilterTests`, `DomMutationNotificationTests`, and `DomPerformanceBenchmarkRunnerTests`: pass (`8/8`).

### 1.67 Precomputed Element Ancestor Features (2026-07-14)

- `FenBrowser.Core/Dom/V2/Element.cs`
  - Attaching a child previously recomputed its parent's ancestor bloom metadata from tag, ID, and every class token. Stable parents repeatedly performed the same casing, concatenation, token traversal, and FNV hashing work.
  - Each element now owns one precomputed 64-bit feature value. Construction initializes the immutable tag contribution; ID/class add, value-change, and removal paths recompute the value before the existing forced descendant refresh. `ComputeFeatureHash` is therefore a constant-time field read during attachment.
  - This is eagerly maintained selector metadata, not an evictable lookup cache: ownership and mutation remain on the DOM element, memory cost is one `long` per element, and there is no global lifetime, key table, stale-entry policy, or cross-thread synchronization.
- `FenBrowser.Tests/Core/AncestorFilterTests.cs`
  - Extends included coverage through ID/class mutation and removal, exact descendant-filter refresh, and positive/negative selector results.
- `FenBrowser.Tests/Performance/DomPerformanceBenchmarkRunnerTests.cs`
  - Adds the same broad allocation ceiling to plain and feature-rich attachment paths.

Five-process Release medians compare feature-rich baseline reports `114446`-`114450` with retained reports `114820`-`114824`:

| Workload | Time before | Time after | Allocation before | Allocation after | Gen0 before | Gen0 after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| feature-rich 10,000 append/remove cycles | 7.769 ms | 1.787 ms (-77.00%) | 3,664,232 B | 943,856 B (-74.24%) | 2 | 0 |
| plain 10,000 append/remove cycles | 2.599 ms | 1.824 ms (-29.82%) | 944,016 B | 943,856 B (-0.02%) | 0 | 0 |

Relative to the original plain DOM baseline, all retained DOM units reduce allocation by 83.10% (`5,584,016 B` to `943,856 B`) and median time by 61.63% (`4.754 ms` to `1.824 ms`).

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `AncestorFilterTests` and `DomPerformanceBenchmarkRunnerTests`: pass (`5/5`).
- Both retained attachment workloads complete 20,000 mutations with zero Gen0/1/2 collections.

### 1.68 Allocation-Free Suppressed Compatibility Logging (2026-07-14)

- `FenBrowser.Core/EngineLogCompat.cs`
  - Allocation tracing of the deterministic render fixtures ranked compatibility logging and legacy-category conversion on the paint path even though normal Release runs had no matching sink.
  - The compatibility entry point now asks the authoritative `LogManager` whether the category and level are enabled before capturing ambient fields, allocating the fallback field dictionary, constructing source metadata, or entering the sink pipeline.
  - The check deliberately uses the configured logger rather than the compatibility facade's cached `IsEnabled` value. Callers that configure `EngineLog` directly therefore retain their existing observable traces.
- `FenBrowser.Core/Logging/EngineLogContracts.cs`
  - Legacy flag-to-subsystem mapping now uses direct bit tests instead of repeated `Enum.HasFlag` calls. Category priority, combined flags, and the General fallback are unchanged; the mapping no longer boxes the enum on the hot check path.
- `FenBrowser.Tests/Logging/EngineLogSettingsTests.cs`
  - Measures 10,000 constant-message compatibility calls with logging disabled and with Debug filtered by an Info threshold. The original path allocated `4,320,000 B` (`432 B/call`); both retained paths allocate `0 B`.

This is an early-exit optimization rather than removal of diagnostics. Enabled messages still flow through the existing context capture, source attribution, filtering, ring-buffer, and sink contracts.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- Included compatibility allocation checks: pass (`2/2`, exactly `0 B` for both suppressed cases).
- Logging contract slice spanning settings, script, parser, event-loop, missing-API, and navigation traces: pass (`9/9`).
- A post-change GC allocation trace no longer contains `EngineLogCompat` or `EngineLogCompatibility.FromLegacyCategory` in the filtered top allocation stacks.

### 1.69 Caller-Lazy Interpolated Compatibility Logging (2026-07-14)

- `FenBrowser.Core/Logging/EngineLogInterpolatedStringHandler.cs`
  - Adds a standard C# interpolated-string handler that snapshots the authoritative category/level filter before evaluating formatted expressions.
  - The handler is a stack-only `ref struct` backed by `DefaultInterpolatedStringHandler` only when enabled. It owns no pool, cache, global table, unmanaged handle, or retained buffer.
  - Disabled and filtered calls skip `AppendFormatted`, including user `ToString`, substring, URI formatting, and numeric formatting work. Enabled calls build the same string and enter the existing compatibility pipeline.
- `FenBrowser.Core/EngineLogCompat.cs`
  - Adds the explicit category-first overload `Log(category, level, $"...")`. The existing string-first APIs remain source- and behavior-compatible.
  - Caller member, file, and line attribution are captured on the handler overload and forwarded to the structured event. Unlike the legacy convenience wrappers, migrated sites therefore identify the real caller instead of the facade. The existing logger check is repeated at emission to remain safe if configuration changes between handler construction and the write.
- `FenBrowser.Tests/Logging/EngineLogSettingsTests.cs`
  - Verifies disabled and severity-filtered handlers do not call a formatted object's `ToString` and allocate exactly `0 B` for 10,000 varying numeric messages.
  - Verifies enabled Debug formatting runs once and emits the original Paint category, Debug level, and formatted message with the actual caller file, member, and line.

The equivalent pre-handler caller path allocated `879,920 B` for 10,000 suppressed varying messages. The retained path allocates `0 B`; this is distinct from the earlier callee-side metadata saving because no message string is created at all.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- `EngineLogSettingsTests`: pass (`8/8`).
- Logging trace contract slice: pass (`12/12`) with enabled parser, script, event-loop, navigation, and missing-API diagnostics preserved.

### 1.70 Same-Object Live Child Node Lists (2026-07-14)

- `FenBrowser.Core/Dom/V2/ContainerNode.cs`
  - `ContainerNode.ChildNodes` previously constructed a `LiveChildNodeList` on every property read. Allocation traces found the getter in dirty-state clearing, paint-tree construction, layout, and selector traversal, where its exclusive allocation weight reached `3.11%`.
  - Each container now lazily owns one live wrapper. This implements the repository IDL's `[SameObject]` requirement for `Node.childNodes`; the wrapper continues to read current sibling storage, so append/remove operations remain visible without replacing or invalidating the view.
  - Ownership follows the existing single DOM-owner-thread contract. This is one per-node standards object rather than a global cache: there is no key table, eviction, cross-document lifetime, or pooled state. Unread collections remain unallocated; accessed containers retain one wrapper and one reference field.
- `FenBrowser.Tests/Core/ChildNodeListTests.cs`
  - Proves repeated reads return the same object, that the object remains live across two mutations, and that 10,000 post-initialization reads allocate `0 B`.

Five-process Release medians compare immediate reports `124343`-`124348` with retained reports `124856`-`124901`:

| Scenario | Total time before | Total time after | Paint allocation before | Paint allocation after | Managed allocation before | Managed allocation after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 183.50 ms | 183.16 ms (-0.19%) | 1,899,952 B | 1,622,272 B (-14.62%) | 22,328,272 B | 21,871,048 B (-2.05%) |
| steady-state-damage-animation | 14.26 ms | 14.17 ms (-0.63%) | 1,140,082 B | 971,602 B (-14.78%) | 18,020,288 B | 17,081,336 B (-5.21%) |
| dense-text-flow | 23.56 ms | 20.62 ms (-12.48%) | 2,543,036 B | 1,700,356 B (-33.14%) | 11,950,968 B | 10,231,456 B (-14.39%) |
| wrapped-multiline-text | 7.70 ms | 7.63 ms (-0.91%) | 662,636 B | 468,188 B (-29.34%) | 5,166,320 B | 4,684,032 B (-9.34%) |

The direct read probe moved from `240,000 B` to `0 B`. `ContainerNode.get_ChildNodes` disappeared from the top 500 entries in the fresh allocation trace. Working-set medians remained within `-1.31%` to `+0.25%`; the dense fixture's Gen0 count moved from `1` to `0`, while the wrapped fixture moved from `0` to `1`, so no universal GC-pause claim is made.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- Same-object, liveness, allocation, parser, and hardening slice: pass (`20/20`).
- All four benchmark failure gates passed in every retained report.

### 1.71 Lazy HTML Tag-Attribute Storage (2026-07-14)

- `FenBrowser.Core/Parsing/HtmlToken.cs`
  - The post-paint allocation trace ranked `TagToken` construction as the largest FenBrowser allocation leaf (`16.15%`). Every start and end tag eagerly constructed a `List<HtmlAttribute>`, including the common attribute-free case and end tags whose lists remain empty.
  - `TagToken` now creates its mutable attribute list on first observable access or first `AddAttribute`. The public `Attributes` object remains stable after creation and keeps the existing mutable-list API.
  - Internal `HasAttributes` checks let the tree builder skip list materialization when it only needs to enumerate existing attributes. Pooled tokens clear an already-created list without forcing one into existence.
- `FenBrowser.Core/Parsing/HtmlTreeBuilder.cs`
  - Element construction, foreign-attribute adjustment, table foster parenting, meta processing, and attribute-sensitive branches now test `HasAttributes` before reading the list. Token order, duplicate handling, foreign-name adjustment, DOM attribute creation, and malformed-markup recovery are unchanged.
- `FenBrowser.Tests/Core/Parsing/HtmlTokenPoolTests.cs`
  - The 10,000-tag constructor probe failed before the change at `880,000 B` and measures `560,000 B` after it, removing `320,000 B` (`36.36%`, exactly `32 B` per attribute-free token) while still validating lazy list access and mutation.

Five-process Release medians compare immediate reports `124856`-`124901` with retained reports `130712`-`130717`:

| Scenario | HTML allocation before | HTML allocation after | Difference | HTML parse time before | HTML parse time after |
| --- | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 1,127,920 B | 1,114,320 B | -13,600 B (-1.21%) | 34.45 ms | 34.52 ms |
| steady-state-damage-animation | 731,504 B | 723,664 B | -7,840 B (-1.07%) | 1.75 ms | 1.75 ms |
| dense-text-flow | 523,712 B | 512,064 B | -11,648 B (-2.22%) | 0.97 ms | 0.97 ms |
| wrapped-multiline-text | 355,608 B | 350,256 B | -5,352 B (-1.51%) | 0.48 ms | 0.48 ms |

Parser timing is recorded as unchanged/inconclusive; only the deterministic allocation reduction is claimed. Gen0/1/2 collection medians are unchanged in all four fixtures. The post-change verbose allocation trace contains no exclusive `TagToken` constructor allocation stack.

Verification:

- `dotnet build FenBrowser.Core/FenBrowser.Core.csproj -c Release --no-restore -v minimal /nodeReuse:false`: pass (`0` warnings, `0` errors).
- Focused pool, tokenizer, tree-builder, malformed-input, table, select, and html5lib slice: pass (`93/93`).
- All four benchmark failure gates passed in every retained report.

### 1.72 Lazy HTML Token-Pool Slot Storage (2026-07-14)

- `FenBrowser.Core/Parsing/HtmlTokenPool.cs`
  - A retained Release trace attributed `18.3687` sampled units to `HtmlTokenPool` construction. Every tree builder eagerly allocated reference arrays for 4,096 start tags, 4,096 end tags, 16,384 character tokens, 1,024 comments, and 64 doctypes before renting one token. The character array alone was approximately 128 KB and entered the Large Object Heap on every parse.
  - The same bounded ring indices now address `List<T>` slot tables that begin on the shared empty backing array and grow sequentially only when a token type reaches a new high-water mark. Once a table reaches its existing cap, the index wraps and reuses the same token objects as before.
  - Maximum sizes, rental order, token reset behavior, telemetry, end-of-parse `ResetAll`, pool ownership, and tokenizer/tree-builder contracts are unchanged. Slot lists are pool-owned and never exposed; no global cache, unsafe code, external pool, native resource, or concurrency boundary is added.
- `FenBrowser.Tests/Performance/HtmlTokenPoolAllocationTests.cs`
  - Constructing 100 warmed pools moves from exactly `20,560,984 B` to exactly `25,600 B`, saving `20,535,384 B` (`99.88%`, about `205.4 KB` per parse). The retained `26,000 B` ceiling rejects eager maximum-size tables.
  - A full 4,096-entry start-tag cycle verifies that storage grows to the existing bound, the next rental wraps to the first token identity, and allocation/rental telemetry remains exact.

Five fresh Release processes compare the cascade-index reports `155232`, `155233`, `155234`, `155235`, and `155237` with candidate reports `160057`, `160059`, `160100`, `160101`, and `160102`:

| Scenario | Total before | Total after | HTML time before | HTML time after | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 169.94 ms | 171.34 ms (+0.82%) | 32.70 ms | 32.55 ms (-0.46%) | 1,114,320 B | 925,928 B (-16.91%) | 19,568,808 B | 19,377,312 B (-0.98%) |
| steady-state-damage-animation | 15.89 ms | 15.85 ms (-0.25%) | 1.63 ms | 1.64 ms (+0.61%) | 723,664 B | 526,912 B (-27.19%) | 14,951,984 B | 14,753,544 B (-1.33%) |
| dense-text-flow | 12.82 ms | 12.64 ms (-1.40%) | 0.92 ms | 0.91 ms (-1.09%) | 512,064 B | 315,312 B (-38.42%) | 8,922,752 B | 8,761,840 B (-1.80%) |
| wrapped-multiline-text | 6.82 ms | 7.15 ms (+4.84%) | 0.45 ms | 0.44 ms (-2.22%) | 350,328 B | 149,432 B (-57.35%) | 4,263,672 B | 4,063,328 B (-4.70%) |

HTML-stage and whole-process allocation fall in every fixture by the expected per-parser amount. HTML timing ranges from `-2.22%` to `+0.61%`, and total timing is mixed, so no latency improvement is claimed. Gen0/1/2 collection medians are unchanged. The fresh trace removes the pool constructor and reduces sampled `HtmlTokenizer.NextToken` attribution from `133.0734` to `0.3308` units; exact allocation measurements remain the primary evidence.

Verification:

- Existing pool contracts plus the new constructor and bounded-wrap contracts pass `5/5` twice.
- The tokenizer, tree-builder, malformed recovery, tables, selects, interleaved builds, and local html5lib fixture slice passes `95/95`.
- The broader included Core parsing slice remains `68/69`; `HtmlParserTraceTests.ParseDocumentDetailed_WritesHtmlParsingTraceEvents` is the same pre-existing trace-file assertion before and after.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and every benchmark failure gate passes.
- Test262 and WPT are not rerun because this changes only private pool slot storage; local tokenizer/tree-builder output and recovery behavior are covered directly, including html5lib fixtures.

### 1.73 Deferred Character-Data Mutation Records (2026-07-14)

- `FenBrowser.Core/Dom/V2/CharacterData.cs`
  - The post-raster-glyph allocation trace ranked `HtmlTreeBuilder.InsertCharacter` as the largest concrete FenBrowser allocation owner at `139.839` sampled units. Appending to an existing parser text node enters `CharacterData.OnDataChanged`, which previously constructed a `MutationRecord` before inspecting any ancestor for registered observers.
  - Character-data notification now carries a nullable record while walking ancestors. No record exists unless a container actually owns a registered-observer list; once created, the same record is reused for direct-parent and subtree notification as before.
- `FenBrowser.Core/Dom/V2/ContainerNode.cs`
  - The observer-owning container now performs the deferred record construction after its existing null-list check. Direct `CharacterData` filtering, subtree filtering, target, old value, callback scheduling, and record reuse are unchanged.
  - The fast path adds no cache, pool, unsafe code, global state, native resource, or concurrency boundary. DOM ownership remains unchanged, and observed mutations still use the existing thread-safe observer list.
- `FenBrowser.Tests/Performance/CharacterDataMutationAllocationTests.cs`
  - Ten thousand warmed data changes on an attached but unobserved text node move from exactly `880,000 B` to exactly `0 B`, removing `88 B` per mutation (`100%` of the avoidable record cost).
  - A direct parent observer and an ancestor subtree observer both continue to receive exactly one `CharacterData` record with the same text-node target and `"before"` old value.

Five fresh Release processes compare the raster-glyph reports `162921`, `162922`, `162923`, `162924`, and `162925` with candidate reports `163548`, `163550`, `163551`, `163552`, and `163553`:

| Scenario | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after | Total before | Total after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 925,928 B | 925,928 B (flat) | 19,350,568 B | 18,942,208 B (-2.11%) | 171.40 ms | 172.03 ms (+0.37%) |
| steady-state-damage-animation | 526,912 B | 526,912 B (flat) | 14,706,776 B | 14,462,024 B (-1.66%) | 14.18 ms | 14.15 ms (-0.21%) |
| dense-text-flow | 315,312 B | 315,312 B (flat) | 8,733,008 B | 8,687,824 B (-0.52%) | 11.82 ms | 11.93 ms (+0.93%) |
| wrapped-multiline-text | 149,360 B | 149,360 B (flat) | 4,033,744 B | 4,009,992 B (-0.59%) | 6.55 ms | 6.36 ms (-2.90%) |

The exact DOM mutation probe and trace movement are the causal evidence. The benchmark's current-thread HTML counter is flat, while its approximate process-wide counter observes the reduction after the parser phase; the latter is not presented as CSS behavior. Managed allocation falls in every fixture, but total and HTML timings are mixed, so no latency improvement is claimed. The fresh trace reduces `InsertCharacter` from `139.839` to `0.3041` sampled units and exposes `HtmlTreeBuilder.CreateElement` as the next parser owner at `138.831` units.

Verification:

- The allocation and direct/subtree observer contracts pass `2/2` twice on the retained Release build.
- The included MutationObserver and DOM mutation-notification slice passes `6/6`.
- The tokenizer, tree-builder, malformed recovery, tables, selects, interleaved parsing, and local html5lib fixture slice passes `95/95`.
- The broader Core parsing slice remains `68/69` with the same existing `HtmlParserTraceTests` trace-file assertion.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and all four benchmark failure gates pass in every candidate process.
- Test262 and WPT are not rerun because this changes only allocation timing for an existing DOM notification record; observer semantics and parser output are covered directly.

### 1.74 Lazy Element Attribute Maps (2026-07-14)

- `FenBrowser.Core/Dom/V2/Element.cs`
  - `Element` previously constructed a four-inline-slot `NamedNodeMap` for every node even when the element never carried an attribute and callers only used `GetAttribute`, `HasAttribute`, or removal no-ops.
  - Attribute storage is now created on the first collection access or first attribute insertion. Empty read, query, removal, clone, equality, and serialization paths inspect the nullable backing field without materializing it.
  - The public `Attributes` contract remains a non-null, stable, live `NamedNodeMap`: once observed, repeated access returns the same object and later mutations remain visible through it. Attributed elements keep the existing owner and mutation flow.
  - Lazy initialization follows the existing single-owner DOM threading boundary; it adds no cross-thread synchronization, cache, pool, unsafe code, native resource, or global lifetime.
- `FenBrowser.Tests/Performance/ElementAttributeStorageAllocationTests.cs`
  - Ten thousand warmed attribute-free element constructions move from exactly `3,920,000 B` to `3,200,000 B`, saving `720,000 B` (`18.37%`, `72 B` per element). The retained `3,300,000 B` ceiling rejects eager map creation.
  - Focused contracts preserve empty reads, stable/live collection identity, set/remove behavior, clone equality, and empty/attributed serialization.

Five fresh Release processes compare reports `163657`, `163659`, `163700`, `163701`, and `163703` with candidate reports `164347`, `164349`, `164351`, `164354`, and `164356`:

| Scenario | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after | Total before | Total after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 925,928 B | 925,784 B (-144 B) | 18,949,944 B | 18,949,800 B (-144 B) | 172.64 ms | 172.48 ms (-0.09%) |
| steady-state-damage-animation | 526,912 B | 526,768 B (-144 B) | 14,458,088 B | 14,458,024 B (-64 B) | 14.18 ms | 14.52 ms (+2.40%) |
| dense-text-flow | 315,312 B | 302,208 B (-13,104 B) | 8,696,640 B | 8,682,728 B (-13,912 B) | 12.27 ms | 11.88 ms (-3.18%) |
| wrapped-multiline-text | 149,360 B | 143,456 B (-5,904 B) | 4,010,032 B | 4,001,472 B (-8,560 B) | 6.28 ms | 6.51 ms (+3.66%) |

Allocation falls according to the number of unobserved attribute-free elements in each fixture. Total timing remains mixed, so no latency improvement is claimed. A fresh `gc-verbose` trace moves `HtmlTreeBuilder.CreateElement` from `9.74%` to `9.53%` exclusive sampled weight; the exact constructor measurement is the causal evidence and the trace movement is directional.

Verification:

- The allocation and behavior contracts pass `3/3` twice at exactly `3,200,000 B` on the retained Release build.
- The neighboring included DOM/filter/observer slice passes `9/9`; the focused parser regression slice passes `95/95`.
- The broader Core parsing slice remains `68/69` with the same existing `HtmlParserTraceTests` trace-file assertion.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and every render benchmark failure gate passes in all five candidate processes.
- Test262 and WPT are not rerun because the change preserves the DOM attribute API and parser output; the included attribute and parser contracts exercise the affected behavior directly.

### 1.75 Allocation-Free Open-Element Stack Search (2026-07-14)

- `FenBrowser.Core/Parsing/HtmlTreeBuilder.cs`
  - The allocation trace ranked `HtmlTreeBuilder.StackHas` at `0.74%` exclusive sampled weight and showed `Stack<Element>.IEnumerable<Element>.GetEnumerator`. Both `StackHas` and `PopUntil` used capturing LINQ predicates for ordinary open-element membership checks.
  - `StackHas` now iterates the concrete stack directly, and `PopUntil` reuses that helper. Top-to-bottom search order, ordinal-ignore-case matching, absent-target no-op behavior, and the subsequent pop sequence remain unchanged.
  - The parser adds no cache, pool, retained state, unsafe code, recursion, or concurrency boundary; it only avoids the closure, predicate, LINQ iterator, and boxed stack enumerator created for each search.
- `FenBrowser.Tests/Performance/HtmlTreeBuilderStackSearchAllocationTests.cs`
  - A warmed production parse of 2,000 ordinary `<div></div>` elements exercises 4,000 membership searches. Allocation moves from exactly `2,322,168 B` to `1,554,168 B`, saving `768,000 B` (`33.07%`, `192 B` per search).
  - The retained `1,600,000 B` ceiling rejects the LINQ implementation, and the parsed document must still expose its body.

Five fresh Release processes compare reports `164347`, `164349`, `164351`, `164354`, and `164356` with candidate reports `164904`, `164906`, `164909`, `164911`, and `164913`:

| Scenario | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after | HTML time before | HTML time after | Total before | Total after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 925,784 B | 764,120 B (-17.46%) | 18,949,800 B | 18,785,528 B (-0.87%) | 32.80 ms | 32.18 ms (-1.89%) | 172.48 ms | 170.50 ms (-1.15%) |
| steady-state-damage-animation | 526,768 B | 434,224 B (-17.57%) | 14,458,024 B | 14,367,520 B (-0.63%) | 1.61 ms | 1.56 ms (-3.11%) | 14.52 ms | 14.11 ms (-2.82%) |
| dense-text-flow | 302,208 B | 233,088 B (-22.87%) | 8,682,728 B | 8,607,320 B (-0.87%) | 1.02 ms | 0.89 ms (-12.75%) | 11.88 ms | 11.92 ms (+0.34%) |
| wrapped-multiline-text | 143,456 B | 112,352 B (-21.68%) | 4,001,472 B | 3,976,336 B (-0.63%) | 0.45 ms | 0.43 ms (-4.44%) | 6.51 ms | 6.26 ms (-3.84%) |

HTML allocation falls in every fixture, with process-wide managed allocation down `0.63%`-`0.87%`. HTML timing medians are directionally lower, but one whole-render median is `0.34%` higher, so the exact allocation reduction is the primary acceptance evidence. A fresh `gc-verbose` trace no longer lists `StackHas` or the boxed stack enumerator among its top 40 exclusive owners.

Verification:

- The allocation contract passes twice at exactly `1,554,168 B` on the retained Release build.
- The existing focused parser slice plus the new allocation contract passes `96/96`.
- The broader Core parsing slice remains `68/69` with the same existing `HtmlParserTraceTests` trace-file assertion.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and all four benchmark failure gates pass in every candidate process.
- Test262 and WPT are not rerun because this replaces private collection iteration without changing HTML tree-construction decisions; focused malformed, table, select, formatting, tokenizer, and local html5lib coverage exercises the affected parser.

### 1.76 Allocation-Free Three-Phase Mutation Guards (2026-07-15)

- `FenBrowser.Core/Engine/EngineContext.cs`
  - The post-transform Release allocation trace ranked `ContainerNode.AssertNotInRestrictedPhase` at `9.40%` exclusive sampled allocation weight. Every guarded child or attribute mutation passed Measure, Layout, and Paint through `AssertNotInPhase(params EnginePhase[])`, creating a temporary array on the allowed path before any DOM work began.
  - `EngineContext` now has a dedicated three-phase overload that compares the current phase directly. The general `params` overload remains available for other arities, while existing three-argument mutation call sites bind to the allocation-free overload without duplicating the guard in DOM classes.
  - Measure, Layout, and Paint remain forbidden, the exception type and diagnostic text remain unchanged, and Idle or other permitted phases still follow the same mutation path. The change adds no cache, pool, unsafe code, native resource, retained state, synchronization, or ownership change.
- `FenBrowser.Tests/Performance/DomMutationPhaseGuardAllocationTests.cs`
  - Ten thousand warmed three-phase checks move exactly from `1,119,928 B` to `0 B` in each of three fresh Release processes.
  - Ten thousand warmed public append/remove pairs move exactly from `2,239,856 B` to `0 B`, removing all measured allocation from the unobserved mutation loop. Counter-tests verify that both child insertion and attribute setting still throw without mutating state in Measure, Layout, and Paint.

Five fresh Release processes compare reports `051603`, `051605`, `051608`, `051610`, and `051613` with candidate reports `052327`, `052329`, `052331`, `052333`, and `052335`:

| Scenario | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after |
| --- | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 760,256 B | 681,296 B (-78,960 B, -10.39%) | 18,386,248 B | 18,299,904 B (-86,344 B, -0.47%) |
| steady-state-damage-animation | 434,224 B | 387,968 B (-46,256 B, -10.65%) | 14,001,152 B | 13,966,384 B (-34,768 B, -0.25%) |
| dense-text-flow | 233,088 B | 192,320 B (-40,768 B, -17.49%) | 8,441,872 B | 8,366,240 B (-75,632 B, -0.90%) |
| wrapped-multiline-text | 112,352 B | 93,872 B (-18,480 B, -16.45%) | 3,872,488 B | 3,849,576 B (-22,912 B, -0.59%) |

Allocation falls in every fixture by an element-count-dependent amount. Timing samples are unstable, including a transient dense-text HTML-parse cluster, so no parser or page-latency improvement is claimed. The exact mutation contracts and deterministic allocation counters are the causal evidence. A fresh final-code `gc-verbose` trace no longer lists `ContainerNode.AssertNotInRestrictedPhase` or the three-phase `EngineContext.AssertNotInPhase` path among the top 75 exclusive allocation owners.

Verification:

- The allocation and restricted-phase contracts pass `5/5` in three fresh Release processes, with both allocation probes reporting exactly `0 B` each time.
- The included mutation, attribute, child-list, ancestor-filter, and tree-walker slice passes `18/18`; the focused tokenizer/tree-builder/parser slice passes `96/96`.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and every render benchmark failure gate passes in all five candidate processes.
- Test262 and WPT are not rerun because this preserves the existing DOM phase invariant and mutation semantics; direct included tests exercise the guarded child and attribute paths plus focused parser construction.

### 1.77 Buffered HTML Tag-Name Construction (2026-07-15)

- `FenBrowser.Core/Parsing/HtmlTokenizer.cs`
  - The post-CSS-tokenizer allocation trace retained sampled `String.Concat`, `Char.ToString`, and substring allocation below HTML parsing. Source inspection found eight tag-name states rebuilding the immutable `TagToken.TagName` string for every accepted character.
  - Tag-name states now append normalized characters to one tokenizer-owned `StringBuilder` and materialize the public token string exactly once in `EmitCurrentTag`. RCDATA, raw-text, script-data, and escaped-script appropriate-end-tag checks compare the builder directly, and malformed-end-tag recovery replays its characters without an intermediate string.
  - ASCII case folding, token lifetime, token-pool reuse, source positions, attribute parsing, appropriate-end-tag decisions, pending-character order, and malformed recovery remain unchanged. The builder is cleared for every new tag, retained only for the tokenizer lifetime, and remains subject to the existing `8,000,000`-character input limit. The change adds no global atom table, cache, unsafe code, pooling contract, synchronization, or ownership change.
- `FenBrowser.Tests/Performance/HtmlTokenizerTagNameAllocationTests.cs`
  - A warmed production tokenizer with reused tag tokens processes 4,000 31-character ordinary start/end tags. Allocation moves exactly from `7,072,600 B` to `352,904 B` in three fresh Release processes, saving `6,719,696 B` (`95.01%`). The retained `380,000 B` ceiling rejects per-character string reconstruction.
  - The probe verifies every emitted tag count and normalized name. Existing html5lib and tree-builder contracts cover ordinary tags, attributes, self-closing tags, RCDATA, raw text, script data, malformed end-tag replay, tables, selects, and parser hardening.

Five immediate Release reports `063050`, `063052`, `063054`, `063057`, and `063059` compare with reports `065311`, `065313`, `065315`, `065316`, and `065318`:

| Scenario | HTML allocation before | HTML allocation after | Managed allocation before | Managed allocation after | HTML time before | HTML time after | Total before | Total after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| first-frame-heavy-layout | 681,296 B | 620,400 B (-60,896 B, -8.94%) | 17,790,504 B | 17,730,368 B (-0.34%) | 31.08 ms | 29.89 ms (-3.83%) | 139.22 ms | 135.53 ms (-2.65%) |
| steady-state-damage-animation | 387,968 B | 352,032 B (-35,936 B, -9.26%) | 13,495,440 B | 13,463,920 B (-0.23%) | 1.50 ms | 1.47 ms (-2.00%) | 12.61 ms | 12.30 ms (-2.46%) |
| dense-text-flow | 191,208 B | 190,968 B (-240 B, -0.13%) | 6,092,632 B | 6,086,760 B (-0.10%) | 0.81 ms | 0.86 ms (+6.17%) | 7.79 ms | 7.78 ms (-0.13%) |
| wrapped-multiline-text | 94,984 B | 94,568 B (-416 B, -0.44%) | 3,366,776 B | 3,366,680 B (effectively flat) | 0.43 ms | 0.42 ms (-2.33%) | 4.18 ms | 4.03 ms (-3.59%) |

HTML allocation falls in every fixture and HTML time improves in three; dense-text HTML time rises `0.05 ms`, so timing is reported without a universal parser-latency claim. Gen0/1/2 collection counts are unchanged. In the final `gc-verbose` trace, generic two-string `String.Concat` falls from `0.59%` to `0.32%` exclusive sampled weight; the exact production tokenizer probe remains the causal evidence.

Verification:

- The tag-name allocation contract passes three fresh Release processes at exactly `352,904 B` each.
- The included tokenizer, token-pool, html5lib, tree-builder, raw-text, table, select, parser-entrypoint, and hardening slice passes `97/97` in Release.
- Release builds of `FenBrowser.Core` and `FenBrowser.Tooling` succeed with zero warnings and zero errors, and every benchmark failure gate passes in all five retained candidate reports.
- Test262 and WPT are not rerun because this changes private token-name construction without changing HTML parsing decisions; the focused local tokenizer and tree-builder suite exercises every changed state family.

### 1.78 DOMTokenList Ordered-Set Parsing (2026-07-16)

- `DOMTokenList` now parses its associated attribute as an ordered set: ASCII-whitespace-separated tokens retain first-occurrence order and duplicate tokens are omitted from `length`, indexed access, iteration, and token operations.
- The literal associated attribute remains unchanged when read through `value`; parsing duplicate tokens does not normalize or rewrite the stored string.

Verification:

- Red: `DomTokenListValueBindingTests.ValueAssignment_ParsesAnOrderedSetWithoutDuplicateTokens` returned length `3` for `" foo bar foo "`.
- Green: both included `DomTokenListValueBindingTests` pass `2/2`; selected WPT `DOMTokenList-value.html` passes.

### 1.79 AppContainer Child Environment Propagation (2026-07-16)

- `WindowsAppContainerSandbox.SpawnProcess` now passes the `ProcessStartInfo.Environment` map to `CreateProcessW` as a sorted, double-null-terminated Unicode environment block and sets `CREATE_UNICODE_ENVIRONMENT`.
- The custom AppContainer path previously supplied `lpEnvironment = null`, so renderer pipe, authentication-token, parent-PID, sandbox-profile, and capability variables were discarded even when the child executable could launch.
- The managed and unmanaged environment copies are cleared before the native allocation is released because the block contains the renderer authentication token.
- `WindowsAppContainerEnvironmentTests` locks ordering, termination, and preservation of the renderer startup variables. The test validates environment construction; real AppContainer startup remains blocked by runtime-path ACL policy recorded in `BLOCK-PROC-002`.

### 1.80 Evidence-Based Missing-API Classification (2026-07-16)

- `MissingApiClassifier` defines the shared `STANDARD_API`, `SITE_EXPANDO`, `WRONG_RECEIVER`, `LEGACY_PROBE`, and `UNCLASSIFIED` dispositions plus read/write/delete/in/prototype/call/construct/descriptor operation kinds.
- Unknown reads never become standards work by default. Only explicit checked-in-WebIDL membership with a matching receiver sets standards-priority eligibility; assignment-before-read identifies expandos, and a bounded explicit legacy inventory identifies legacy probes.
- The classifier does not change JavaScript return values, add browser members, or suppress observations.
