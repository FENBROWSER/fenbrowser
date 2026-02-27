# FenBrowser Codex - Volume II: The Core Foundation

**State as of:** 2026-02-20
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

### 2.2 Elements (`Element.cs`)

Extends `ContainerNode`. Represents HTML tags.

- **Attributes**: Managed via `NamedNodeMap` with security sanitization.
- **ClassList**: `DOMTokenList` implementation for efficient class toggling.
- **ShadowRoot**: Hooks for Shadow DOM encapsulation.

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

---

## 4. The Parsing Engine (`FenBrowser.Core.Parsing`)

Based strictly on the **HTML5 Parsing Specification**.

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

---

## 5. CSS Types (`FenBrowser.Core.Css`)

This namespace defines the _computed_ values, not the parser (which is in `FenEngine`).

- **CssComputed**: A massive struct/class holding the final resolved values for a node.
- **CssLength**: Struct representing values like `10px`, `50%`, `auto`.
- **CssColor**: Internal representation of RGBA colors.

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

#### `Range.cs` (Lines 1-806)

Represents a continuous fragment of the document.

- **Lines 63-156**: **Boundary Management**: `SetStart`, `SetEnd`.
- **Lines 244-261**: **`DeleteContents`**: Complex logic to remove nodes partially contained in the range.

#### `MutationObserver.cs` (Lines 1-507)

Asynchronous DOM mutation observation.

- **Lines 39-79**: **`Observe`**: Registers interest in specific mutation types (Attributes, ChildList).
- **Lines 137-160**: **`EnqueueRecord`**: Thread-safe queueing of changes.
- **Lines 436-483**: **Filter Logic**: `GetObserversForNotification`.

#### `NamedNodeMap.cs` (Lines 1-344)

Optimized attribute map storage for Elements.

#### `Attr.cs` (Lines 1-120)

Represents a DOM attribute node (Legacy DOM Level 1).

- Wraps the attribute data stored in `Element`.

#### `CharacterData.cs` (Lines 1-350)

Abstract base for `Text`, `Comment`, and `ProcessingInstruction`.

- **Lines 80-150**: **`AppendData`**, `DeleteData`, `ReplaceData`: String manipulation logic.

#### `Comment.cs` (Lines 1-45)

Represents HTML comments (`<!-- ... -->`).

#### `DOMTokenList.cs` (Lines 1-301)

Efficient `class` attribute parsing (Space-separated tokens).

- **Lines 40-80**: **`Add`**: Token insertion.
- **Lines 120-150**: **`Toggle`**: Conditional addition/removal.

#### `DocumentFragment.cs` (Lines 1-90)

Lightweight container for nodes (not part of the main tree).

#### `DocumentType.cs` (Lines 1-60)

Represents the `<!DOCTYPE html>` node.

#### `DomException.cs` (Lines 1-150)

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

- **`CssColor.cs`**: Helper struct for RGBA color parsing and storage.
- **`CssComputed.cs`**: The large struct holding resolved CSS properties for a node.
- **`CssLength.cs`**: Struct for numeric values with units (px, em, %).
- **`CssPropertyNames.cs`**: Enum/Constants for known CSS properties.

#### Helpers & Security (`FenBrowser.Core.Dom.V2`)

- **`Mixins.cs`**: Shared logic for `IParentNode` and `IChildNode`.
- **`NodeFlags.cs`**: Bitflags for optimizing node state checks (IsConnected, HasChildren).
- **`TreeScope.cs`**: Manages ID lookup boundaries (Document vs ShadowRoot).
- **`Security/Origin.cs`**: Value object for scheme/host/port tuples.
- **`Security/SecurityGuard.cs`**: Checks Cross-Origin access rules.
- **`Security/AttributeSanitizer.cs`**: Validates attribute names/values; inline `on*` handler values are preserved by default for browser compatibility, with opt-in strict blocking via `BlockInlineEventHandlersInStrictMode`.
- **`Selectors/CssSelector.cs`**: AST for parsed CSS selectors.
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

#### `NetworkClient.cs` (Lines 1-308)

Optimized HTTP client wrapper.

- **Lines 41-92**: **`SendAsync`**: Connection pooling logic.
- **Lines 202-255**: **Statistics**: Tracks headers/bytes for developer tools.

#### `ResourcePrefetcher.cs` (Lines 1-503)

Handles `<link rel="preload">` and `prefetch`.

- **Lines 104-146**: **`PrefetchFromDomAsync`**: Scans DOM for hint tags.
- **Lines 280-340**: **`ExecutePrefetchAsync`**: Background low-priority fetcher.

#### `MimeSniffer.cs` (Lines 1-184)

Determines content type when headers are missing/wrong.

- **Lines 68-135**: **`SniffFromMagicBytes`**: Checks file signatures (e.g., JPEG, PNG, PDF headers).

### 6.4 Infrastructure (`FenBrowser.Core`)

#### `CacheManager.cs` (Lines 1-402)

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
  - `ImproveBrowser` now controls emission of client-hints request headers (`Sec-CH-UA*`) at runtime.
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
