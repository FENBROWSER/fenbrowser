# Cross‑Platform Browser Engine in C# — No‑Compromises Build Guide
**Goal:** design and implement a modern, secure-by-design, modular browser engine in C# with **Chromium/Firefox‑class behavior**, strong isolation, and conformance gates (**Test262 + WPT**)—without shipping half-features.

**Audience:** engine developers building an end‑to‑end stack: HTML/CSS/DOM/JS, networking, rendering, sandboxing, and platform integration.

**Design stance:** The renderer is assumed compromised. Security comes from **process boundaries + least privilege + strict origin/site isolation + capability‑based IPC**, not from “managed code safety”.

---

## 1) Non‑negotiables
### 1.1 Architectural invariants (“engine laws”)
1. **Multi‑process architecture is mandatory.**  
   - Renderer is sandboxed, hostile.  
   - Network, GPU/compositor, and high‑risk parsers run out-of-process.
2. **Site / origin isolation by default.**  
   - A renderer is locked to a site (eTLD+1) or origin.  
   - Cross-site frames eventually become out‑of‑process frames.
3. **No ambient authority.**  
   - All privileged actions require broker-issued **capability handles**.
4. **Spec-first algorithms.**  
   - URL/Fetch/HTML/DOM/Encoding/MIME sniffing behavior must be algorithmically faithful.
5. **No “half features”.**  
   - A feature is OFF until it passes defined gates: conformance, fuzzing, security review, perf budgets.

### 1.2 What “production-grade” means in this project
- **Correctness:** systematic conformance to specs via test suites; tracked divergences with documented rationale.
- **Security:** defense-in-depth; sandbox + IPC validation + strict origin boundaries + minimized native attack surface.
- **Performance:** data-oriented core structures; caching/invalidation discipline; multi-threaded rendering; low jank.

---

## 2) Cross‑platform process model (baseline)
### 2.1 Required processes (minimum viable secure architecture)
1. **Browser (Broker / UI / Policy)**  
   - permissions, navigation, site isolation rules, process management, storage partitioning policy, crash handling.
2. **Renderer (Untrusted Target)**  
   - HTML/CSS/DOM, JS engine, layout, paint → produces **display lists** and resource references.
3. **Network (Target)**  
   - Fetch stack, HTTP cache, cookies, proxy, DNS, TLS, service integration.
4. **GPU / Compositor (Target)**  
   - raster + compositing + presentation; isolates native graphics stacks.
5. **Utility (Target)** *(split as needed)*  
   - image decode, font decode, media parsing, PDF, etc. (high-risk parsers live here).

### 2.2 Cross‑platform sandbox strategy (high level)
You cannot use a single OS-specific sandbox and call it done. Instead:
- Define a portable **Security Abstraction Layer (SAL)** describing allowed capabilities.
- Implement per‑OS sandboxes as “best available hardening”:
  - Windows: AppContainer + job limits + token restrictions.
  - Linux: namespaces + seccomp-bpf + no network namespace for renderer.
  - macOS: App Sandbox + least entitlements + hardened helpers.

**Important:** capability discipline must hold even if an OS sandbox is weaker than desired; you compensate with broker enforcement and strict IPC checks.

---

## 3) SAL + PAL (how to stay secure and portable)
### 3.1 Security Abstraction Layer (SAL)
SAL defines security policy in OS‑agnostic terms.

**Core SAL concepts**
- `ProcessRole`: Browser | Renderer | Network | GPU | UtilityFont | UtilityImage | UtilityMedia | …
- `SiteLock`: `(scheme, registrable_domain, origin)` lock for each renderer.
- `CapabilityHandle`: opaque broker-minted handles (unforgeable IDs).
- `Policy`: allowlists and constraints (network destinations, storage partitions, device access, file scopes).

**SAL rules**
- Renderer starts with **zero** privileges.
- Renderer requests operations via IPC; broker verifies:
  - `SiteLock` matches
  - user gesture state (if required)
  - permission state
  - policy allowlists
- Broker returns a capability handle representing a constrained permission.

### 3.2 Platform Abstraction Layer (PAL)
PAL provides a strict interface for OS operations:
- spawning + applying sandbox profiles
- IPC transports (pipes/sockets/shm)
- shared memory + handle transfer
- clocks, threads, IO event loops (IOCP/epoll/kqueue)
- windowing + surface creation
- crash reporting plumbing

**Rule:** core engine components must not call OS APIs directly—only through PAL.

---

## 4) IPC design (secure + fast)
IPC is a security boundary; treat all IPC input as hostile.

### 4.1 Two‑plane model: Control + Data
**Control plane:** small messages via sockets/pipes  
- capability requests, lifecycle, acknowledgements, scheduling.

**Data plane:** shared memory for large payloads  
- display lists, glyph buffers, image bitmaps, large serialization blobs.

### 4.2 Capability-based IPC
**Never** pass raw OS handles or file paths to the renderer without broker mediation.

Examples:
- Renderer → “Request clipboard write” (requires user gesture) → Broker returns `ClipboardWriteHandle`.
- Renderer → “Fetch X” → Network process does the fetch; renderer only receives a safe response stream handle.

### 4.3 Shared memory protocol (ring buffer or slab allocator)
For graphics and other large data transfers:
- Use shared memory ring buffers or immutable slabs.
- Harden it like a storage engine:
  - fixed header: version, sizes, offsets
  - sequence numbers
  - CRC/xxHash for frames
  - two-phase commit: write payload → publish commit record atomically
  - strict bounds checking on the consumer side

**Linux hardening:** memfd with seals (immutable submission).  
**Windows/macOS:** emulate immutability via consumer-only read mapping or copy-on-submit for security-sensitive payloads.

### 4.4 IPC fuzzing (mandatory)
- Create fuzzers per endpoint and for the shared-memory protocol frames.
- Run continuously in CI, on all platforms.

---

## 5) Memory management: “Bypass GC” the right way
You will allocate **millions** of short-lived objects: styles, layout boxes, display list items, parser tokens.

### 5.1 Arena allocation strategy (recommended)
Use arena allocators for high churn, frame/epoch-lifetime data:
- **Style computation artifacts**
- **Layout boxes**
- **Display list nodes**
- **Temporary parse structures**

Implementation techniques:
- unmanaged blocks (`NativeMemory` / `malloc` equivalent) or large managed arrays from pools
- safe surface API via `Span<T>` and `Memory<T>`
- per-epoch “free all” semantics

### 5.2 DOM in unmanaged memory: handle‑only (no raw pointers)
DOM has complex lifetimes: events, microtasks, JS wrappers, adoption between documents, bfcache, etc.

If you store DOM nodes in unmanaged arenas:
- use `(arenaId, index, generation)` handles
- generation increments on free/reuse
- wrapper objects store only the handle + realm pointer
- every access validates generation + site lock

### 5.3 Hardening and debugging
- Use guard pages / canaries for debug builds (where possible).
- Add “poisoning” patterns on free to detect UAF.
- Add stress modes:
  - aggressive GC mode
  - randomized allocation sizes
  - periodic forced teardown/restore for bfcache simulation

### 5.4 Practical compromise
- Start by arena-allocating **layout/style/paint** only.
- Keep DOM in managed memory early unless you already have handle discipline and lifetime semantics fully engineered.

---

## 6) JavaScript engine: Test262-grade plan
### 6.1 Phased execution engine strategy (no shortcuts)
1. **Baseline bytecode VM** (correctness-first)
2. Add **Inline Caches (ICs)** and shapes/hidden classes
3. Add **mid-tier JIT** only after conformance and deopt infrastructure is solid
4. Add optimizing tier last

### 6.2 “JIT-on-JIT” reality and mitigation
You will have .NET JIT plus JS tiering. To control variability:
- warmup runs in CI
- profile-guided improvements
- consider AOT for non-renderer processes (broker/network), but keep renderer flexible early

**Avoid early LLVM JIT** unless you can justify:
- security hardening of JIT pages (W^X discipline)
- deployment and patching of libLLVM
- compilation latency and jitter impact

### 6.3 Correctness requirements
Must implement:
- strict/sloppy mode semantics
- full scope rules and TDZ
- modules (ESM linking/instantiation/evaluation)
- Proxy/Reflect correctness
- TypedArrays/ArrayBuffer, BigInt
- RegExp correctness and DoS hardening
- GC correctness: WeakRef/FinalizationRegistry semantics

### 6.4 Conformance workflow (Test262)
- pin Test262 commit hash
- expected-fail ledger with reasons + owner
- “no new failures” rule per PR
- nightly full run and regression bisect support

---

## 7) WebIDL bindings: the spine of Web APIs
WebIDL is the contract between the JS engine and the DOM/Web APIs.

### 7.1 Bindings must be generated
- parse IDL sources
- generate:
  - prototypes/constructors
  - overload resolution
  - type conversions and exception mapping
  - promise bridging
  - brand checks/internal slots

### 7.2 Micro-details to implement correctly
- “same object” caching semantics for some getters
- legacy attributes (where required)
- `this` binding rules
- cross-realm wrapper identity policies (frames/realms)

---

## 8) HTML + DOM: algorithmic fidelity
### 8.1 HTML parsing
- implement tokenizer state machine
- implement tree builder insertion modes
- quirks mode triggers
- mis-nesting/foster parenting behavior
- streaming input support and allocation limits for hostile inputs

### 8.2 DOM core
- Node tree + mutation operations
- EventTarget and full event dispatch (capture/target/bubble)
- MutationObserver with correct microtask timing
- Range/Selection correctness
- Shadow DOM (only when you can do it fully and correctly)

### 8.3 Event loop integration
- multiple task queues by source
- microtask checkpoint rules
- timers and ordering semantics
- navigation and parser interactions

**Rule:** microtask timing must match spec; many “almost browsers” fail here.

---

## 9) CSS, style, layout: correctness + speed
### 9.1 CSS pipeline (real browser pipeline)
1. tokenize  
2. parse rules  
3. compile selectors  
4. cascade resolution  
5. compute values  
6. build layout tree  
7. layout  
8. paint display list  
9. compositing  
10. raster  

### 9.2 Selector matching and invalidation (where performance is won)
Selector matching:
- right-to-left matching
- compiled selector bytecode
- ancestor filters (bloom filters / bitsets)
- memoization for common selectors

Invalidation:
- dependency map for properties (layout vs paint vs composite)
- targeted subtree invalidation for class/id/attribute changes
- incremental recomputation

### 9.3 Layout engine strategy
- keep layout tree separate from DOM (data-oriented)
- incremental layout with dirty bits
- deterministic reentrancy handling (JS can mutate during layout—must define rules)

---

## 10) Rendering architecture: display list + compositor + GPU process
### 10.1 Renderer output
Renderer produces:
- display list (commands)
- references to shared resources (images, glyphs, gradients)
- compositing hints (layers)

### 10.2 Compositor rules
- compositor thread handles scrolling and animations as independently as possible
- maintain stable frame pacing targets
- never block compositor on main thread IO/decode

### 10.3 GPU process rules
- GPU process validates all display list inputs
- raster happens out-of-process
- isolate native stacks (drivers, decoders) from renderer

---

## 11) Text shaping and typography (the “text engine” reality)
Browsers are, in practice, text layout engines.

### 11.1 Shaping requirements
- complex scripts (Arabic, Devanagari, Thai, etc.)
- ligatures, kerning, glyph clusters
- accurate grapheme/cluster mapping (selection, caret)
- bidi algorithm integration

### 11.2 Recommended approach
- integrate a cross-platform shaper (e.g., HarfBuzz) for consistent shaping behavior
- keep shaping output as immutable “runs”:
  - glyph IDs, advances, offsets, clusters, direction, script, language

### 11.3 Consistency caveat
Even with a consistent shaper, pixel-perfect cross-platform identity can diverge due to rasterization and fonts. Mitigate via:
- consistent fallback strategy
- test font packs for WPT
- isolate raster paths for reproducibility testing

---

## 12) Accessibility (A11y) as a first-class tier
### 12.1 Correct mental model
A11y is not purely layout-derived. It depends on:
- DOM semantics + ARIA attributes
- focus/selection state
- visibility and geometry from layout

### 12.2 Incremental A11y design
- build internal AX tree with two layers:
  - semantic layer (DOM/ARIA)
  - geometry layer (layout)
- reuse invalidation:
  - DOM/ARIA mutations → semantic invalidation
  - style/layout changes → geometry invalidation
  - focus/selection changes → state invalidation

### 12.3 Platform bridges
- map internal AX tree to OS APIs:
  - Windows UIA
  - macOS NSAccessibility
  - Linux AT-SPI/ATK

---

## 13) Networking: Fetch + URL + Encoding + MIME sniffing
### 13.1 URL (security-critical)
Implement parsing and serialization according to the standard and avoid “custom rules”.
URL bugs are origin bugs.

### 13.2 Fetch in a dedicated process
Network process owns:
- HTTP stack, redirects, cache
- cookies and credential policy
- CORS and preflight logic
- response tainting (opaque/basic/cors)
- referrer policy integration
- streaming bodies

### 13.3 Encoding + MIME sniffing
- implement encoding tables and decode/encode algorithms
- implement MIME sniffing precisely and integrate `nosniff` semantics properly

---

## 14) Storage and privacy: partition-by-default
### 14.1 Storage partitioning is foundational
Architect everything around partition keys:
- top-level site
- frame site
- network isolation key (if used)

### 14.2 Components
- cookies (domain/path/samesite/secure correctness)
- HTTP cache partitioned
- localStorage/sessionStorage
- IndexedDB + quota manager
- permissions storage tied to site/origin policies

---

## 15) .NET deployment strategy (cross‑platform and secure)
### 15.1 Process-specific runtime choices
- Renderer: keep flexible (JIT) early; tighten later.
- Broker/network/utility: consider Native AOT if your code can avoid dynamic features.

### 15.2 AOT constraints
Native AOT forbids certain runtime dynamism (e.g., reflection emit), so design processes intended for AOT accordingly.

### 15.3 “Safety vs speed” policy
- default to safe constructs (`Span<T>`, pooling, SoA)
- allow `unsafe` in hot loops **only after profiling**
- require a formal review gate for `unsafe`:
  - security lead review
  - fuzz tests added/updated
  - debug hardening enabled

---

## 16) Testing and conformance gates (the enforcement system)
### 16.1 Test262 for JS
- pin a commit hash
- expected-fail ledger
- PR gates: no new failures; changes must include added tests or proven reductions

### 16.2 WPT for web platform
- run per-area suites aligned to modules
- maintain reftest infrastructure (pixel tests) where applicable
- keep expected-fail shrinking; “fail-open forever” is not allowed

### 16.3 Differential testing
For parsers (HTML/CSS/URL/MIME):
- run identical corpora through your implementation and a reference implementation
- compare trees/serialization outputs
- keep corpora versioned and expanded via fuzzing discoveries

### 16.4 Fuzzing
- HTML tokenizer/treebuilder fuzz
- CSS tokenizer/parser fuzz
- URL parser fuzz
- IPC message fuzz
- shared memory frame fuzz
- file format fuzz (fonts/images/media) in utility processes

---

## 17) Performance plan: “on par with C++ engines” in practice
### 17.1 Data-oriented core
- minimize per-node managed objects
- use handles + packed arrays for hot structures
- store computed styles as compact structs with bitsets

### 17.2 Caches and invalidation
- selector matching caches
- property-dependency invalidation
- layout caching and incremental recompute
- painting caches where safe

### 17.3 Scheduling and frame pacing
- define time budgets (e.g., 16.6ms at 60Hz) and enforce them with tracing
- cancel/restart incremental work when inputs change
- keep compositor smooth even under JS load

### 17.4 Instrumentation
- tracing (timeline spans)
- counters (allocations, style recalc counts, layout passes)
- jank detector and “long task” metrics
- crash keys for triage (site lock, process role, last IPC message type)

---

## 18) Feature delivery discipline: “No half features”
### 18.1 Feature flags and staged rollout
Every feature is behind a flag:
- **OFF by default**
- enabled only after:
  - WPT/Test262 gates
  - fuzzing hours threshold
  - security checklist completed
  - perf budgets met

### 18.2 Definition of Done (DoD) template
A PR is “done” only if it includes:
- spec mapping (algorithm sections referenced)
- conformance tests updated/passing
- new fuzz cases if parser/IPC touched
- perf regression results for hot paths
- security review signoff if privilege/IPC/unsafe code changes
- telemetry updates if new subsystem added

---

## 19) Suggested repository layout
```
/src
  /PAL
    /Windows
    /Linux
    /Mac
  /SAL
  /IPC
    /Schema
    /Runtime
    /Fuzz
  /BrowserProcess
  /RendererProcess
    /HTML
    /DOM
    /CSS
    /Layout
    /Paint
    /JS
    /WebIDL
    /A11y
  /NetworkProcess
    /Fetch
    /Cookies
    /Cache
    /TLS
  /GPUProcess
  /Utility
    /ImageDecode
    /FontDecode
    /MediaParse
/tests
  /unit
  /wpt-runner
  /test262-runner
  /fuzz
/tools
  /spec-sync
  /wpt-sync
  /triage
/docs
  ARCHITECTURE.md
  SECURITY_MODEL.md
  PROCESS_MODEL.md
  FEATURE_DOD.md
```

---

## 20) Practical milestone roadmap (vertical slices)
### Milestone A — Secure shell
- PAL/SAL, sandbox scaffolding, IPC discipline
- URL + minimal Fetch wiring in Network process
- renderer skeleton with strict site locks

**Gate:** sandbox verification, IPC fuzz baseline, no privilege leaks.

### Milestone B — JS foundation + bindings seed
- baseline VM, core builtins
- minimal WebIDL generation
- DOM manipulation via bindings

**Gate:** meaningful Test262 subset; deterministic semantics.

### Milestone C — HTML parsing + event loop
- full HTML tokenizer/treebuilder
- event loop + microtasks
- basic navigation

**Gate:** WPT parsing + DOM/event subsets.

### Milestone D — CSS + layout + paint
- CSS pipeline, block/inline layout, text shaping integration
- display list output + compositor presentation

**Gate:** WPT CSS/layout subsets + reftests for implemented features.

### Milestone E — Fetch/CORS/security headers
- CORS/preflight, referrer policy, mixed content, nosniff integration

**Gate:** WPT fetch/cors subsets; origin boundary tests.

### Milestone F — Hardening + expansion
- OOPIF planning, CORB-like defenses
- storage partitioning, service workers (only when you can do them fully)

**Gate:** measured conformance growth + security audit milestones.

---

## 21) Decision review: the six proposals you listed
### 21.1 Arena allocator / bypass GC
**Adopt** for layout/style/paint early.  
**DOM** only after handle+generation lifetime hardening exists.

### 21.2 “JIT-on-JIT”
**Start** with baseline VM + ICs.  
Postpone LLVM until you can fully justify its operational and security costs.

### 21.3 Shared memory for renderer ↔ GPU
**Adopt** for large payloads with two-plane IPC.  
Use immutable handoff patterns; seal on Linux where available; validate always.

### 21.4 Text shaping
**Adopt early**. Without proper shaping, real-world compatibility collapses.

### 21.5 Accessibility as core
**Adopt early** with incremental invalidation; it’s not purely layout-derived.

### 21.6 Unsafe in hot loops
**Allow only** after profiling; enforce “Safety Audit” and fuzz + security review.

---

## 22) References (starting set)
Specs and suites:
- HTML: https://html.spec.whatwg.org/
- DOM: https://dom.spec.whatwg.org/
- Fetch: https://fetch.spec.whatwg.org/
- URL: https://url.spec.whatwg.org/
- Encoding: https://encoding.spec.whatwg.org/
- MIME Sniffing: https://mimesniff.spec.whatwg.org/
- WebIDL: https://webidl.spec.whatwg.org/
- WPT: https://github.com/web-platform-tests/wpt
- Test262: https://github.com/tc39/test262

Security architecture references:
- Chromium sandbox design: https://chromium.googlesource.com/chromium/src/+/HEAD/docs/design/sandbox.md
- Chromium site isolation: https://www.chromium.org/developers/design-documents/site-isolation/

Platform sandbox references:
- Windows AppContainer: https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation
- Linux sandboxing (Chromium doc): https://chromium.googlesource.com/chromium/src/+/0e94f26e8/docs/linux_sandboxing.md
- macOS App Sandbox: https://developer.apple.com/documentation/security/app_sandbox

C# / runtime:
- NativeMemory API: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory
- Memory-mapped files: https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files
- GC latency modes: https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency

Text shaping:
- HarfBuzz: https://github.com/harfbuzz/harfbuzz
- HarfBuzzSharp: https://www.nuget.org/packages/HarfBuzzSharp/

Shared memory hardening (Linux):
- memfd_create(2): https://man7.org/linux/man-pages/man2/memfd_create.2.html
