# fastrender: The Exhaustive Technical Encyclopedia

This is the definitive, exhaustive guide to the `fastrender` engine. It consolidates all six parts of the technical analysis into a single unified reference, preserving every technical detail, file reference, and architectural insight discovered during the deep-dive into its 1,100,000+ lines of source code.

---

## 1. Core Architecture & Orchestration

The engine follows a strict multi-stage pipeline, orchestrated primarily in `src/api.rs`.

### The Pipeline Lifecycle

1.  **Parse (`src/dom.rs`, `src/dom2/`)**: HTML is parsed into a live DOM. `dom2` tracks incremental mutations for invalidation.
2.  **Style (`src/style/`)**: CSS is cascaded. Computed styles are attached to the DOM nodes.
3.  **Box Tree (`src/tree/box_tree.rs`)**: The styled DOM is converted into a box tree. This includes "anonymous box fixup" (e.g., wrapping loose text in block-level containers).
4.  **Layout (`src/layout/`)**: Rectangular "fragments" are calculated. This is the most computationally expensive stage.
5.  **Display List (`src/paint/display_list_builder.rs`)**: Fragments are converted into high-level paint commands (Display items).
6.  **Rasterize (`src/paint/display_list_renderer.rs`)**: Display items are executed into a pixel buffer (Pixmap) using `tiny-skia`.

---

## 2. The Layout Engine: The Multi-Contexual Solver

`fastrender` handles layout by delegating to specific "Formatting Contexts" (FCs).

### Base Abstractions (`layout/formatting_context.rs`)

The `FormattingContext` trait is the core contract. To optimize high-concurrency layout, it uses a **64-shard intrinsic sizing cache**. This allows 16+ Rayon threads to perform "min-content" and "max-content" probes without significant lock contention.

### Block Formatting Context (BFC) (`layout/contexts/block/mod.rs`)

- **Margin Collapsing**: Implemented in `margin_collapse.rs`. It uniquely tracks positive and negative components separately using a `CollapsibleMargin` struct to correctly resolve complex CSS2.1 margin rules.
- **Float Intrusion**: Handled in `inline/float_integration.rs`. Line boxes are dynamically shortened at different Y-offsets based on the current "Float Context" boundary.
- **Recursive Depth Management**: The BFC solver includes cognitive complexity mitigations and recursion caps (depth 32) to prevent stack overflows on adversarial documents.

### Inline Formatting Context (IFC) (`layout/inline/baseline.rs`)

- **The Strut**: Implements the "strut" concept—an invisible zero-width box that ensures every line reaches the minimum height specified by the parent's `line-height`.
- **Vertical Alignment**: A multi-pass algorithm (`BaselineAligner`) calculates the line baseline first, then positions boxes relative to it (`baseline`, `middle`, `text-top`, etc.).

### Flex & Grid (`layout/taffy_integration.rs`)

- **Adapter Pattern**: FastRender uses an adapter pattern to wrap the Taffy engine. It includes high-fidelity usage counters (`TaffyPerfCounters`) to track compute time and "measure" call counts for performance diagnostics.

---

## 3. The Painting Pipeline

### Display List Construction (`paint/display_list_builder.rs`)

The builder is a 23,000+ line module that implements the official CSS Paint Order (Appendix E).

- **Z-Index Layering**: Stacking contexts are created for elements with `opacity < 1`, `transform`, `filter`, or explicit `z-index`.
- **Image Pre-decoding**: Integrated with an LRU `DecodedImageCache` that caps both entry count (default 256) and memory usage (default 128MB).
- **Parallel Preparation**: Supports `FASTR_DISPLAY_LIST_PARALLEL`, allowing sub-trees to be converted to display items in parallel before a final merge.

### Rasterization Backend (`paint/display_list_renderer.rs`)

- **Oklab/Oklch Support**: Advanced implementation of perceptually uniform color spaces for gradients, avoiding the "gray dead zone" of sRGB.
- **Manual Blending**: Hand-optimized ASM-like loops for blend modes not natively supported by `tiny-skia`.
- **Tile Halo Management**: Calculates a `conservative_tile_halo_px` by scanning the display list for effects (shadows, blurs) that bleed beyond their bounds, ensuring seamless parallel tiling.

---

## 4. The Live DOM (`src/dom2/`)

Unlike the static renderer core, `dom2` provides a mutable, spec-compliant DOM environment designed for JavaScript integration.

### Incremental Mutation Tracking

The most critical feature of `dom2` is the `MutationLog`. It records:

- **Attribute Changes**: Keys are normalized to lowercase for HTML compatibility.
- **Structural Moves**: Automatically detects nodes that were removed and re-inserted in the same "log window" as `nodes_moved`.
- **Composed Tree Mutations**: Tracks Shadow DOM slot distribution changes separately from light DOM structural changes.

### Script Lifecycle & Shadow DOM

- **Parser-Inserted Scripts**: Implements the complex "prepare a script" state machine from the HTML spec, tracking `script_already_started` and `script_force_async`.
- **Inert Subtrees**: Nodes inside `<template>` are marked as `inert_subtree`, preventing the renderer (and script probes) from accidentally treating them as live content.
- **Scoped ID Resolution**: `get_element_by_id_from` respects Shadow DOM boundaries, preventing ID leakage between encapsulated components.

---

## 5. The Styling Engine (`src/style/`)

### The Keystone Cascade (`src/style/cascade.rs`)

At 41,000+ lines, the cascade is the single most complex logical unit in the engine.

- **Ancestor Bloom Filter**: To optimize selector matching (e.g., `div > p`), FastRender uses a bloom filter of ancestor hashes.
  - **Shadow Scoping**: The filter is intelligently reset at Shadow DOM boundaries to improve "fast-reject" rates and avoid false positives from the outer tree.
- **Multi-Phase Probes**: The cascade happens in distinct stages:
  1.  **Find**: Identification of matching rules.
  2.  **Decl**: Resolution of declarations into computed values.
  3.  **Pseudo**: Application of generated content and pseudo-elements.
- **Quirks Mode**: Includes a full quirks-mode emulator (mirroring Chrome/WebKit) for ancient documents.

### Custom Properties & Variables

- **Registry & Store**: Implements a dedicated `CustomPropertyRegistry` and `CustomPropertyStore`.
- **Invalidation Chains**: Tracks which computed values depend on specific CSS variables (`var(--x)`) to allow localized re-styling when a variable is changed at the root.

---

## 6. Performance & Cross-Cutting Concerns

### Memory Management Patterns

- **Arc-Str Everything**: String data (URLs, text runs, tag names) is almost exclusively stored in `Arc<str>` or `Arc<String>` to allow multi-threaded layout and paint without copying text.
- **Local Cache Sharding**: Throughout the engine, global caches (Intrinsic sizing, Shaping, Image Decodes) are sharded (typically 64 shards) to reduce mutex contention in Rayon thread pools.

### The Deadlines System (`src/render_control.rs`)

To prevent "Zombie Renders" (documents that take minutes to layout), the engine uses a global `render_control` heartbeat.

- **Stride Checking**: Most tight loops (Layout, Cascade, Paint) check `active_deadline()` every few hundred iterations.
- **Graceful Timeouts**: If a deadline is exceeded, the engine returns a partial result or a specific `LayoutError::Timeout` rather than crashing or hanging the host process.

---

## 7. Resource Fetching & Networking (`src/resource.rs`, `src/net/`)

`fastrender` implements a robust industrial-grade resource pipeline.

### The Resource Fetcher (`src/resource.rs`)

At ~26,000 lines, this file handles:

- **Protocol Dispatch**: Routes requests to `http:`, `https:`, `data:`, `file:`, or internal `chrome:` schemes.
- **MIME Sniffing**: Implements the WHATWG MIME Sniffing standard to identify content when headers are missing or incorrect.
- **Priority Management**: Prioritizes render-blocking resources (CSS, head scripts) over async resources (images at the bottom of the page).

---

## 8. IPC & Multi-process Architecture (`src/ipc/`, `src/multiprocess/`)

To ensure stability and security, `fastrender` follows a modern multi-process architecture.

### Inter-Process Communication (IPC)

- **Framed Codecs (`ipc/framed_codec.rs`)**: Uses a high-performance binary codec for message serialization.
- **Shared Memory (`ipc/shm.rs`)**: For high-bandwidth data like decoded pixel buffers or raw audio streams, the engine avoids copying by passing file descriptors for shared memory regions.
- **UNIX Seqpacket (`ipc/unix_seqpacket.rs`)**: On Linux, it uses `AF_UNIX` seqpacket sockets for low-latency, ordered, reliable message delivery.

### Site Isolation (`src/multiprocess/subframes.rs`)

`fastrender` implements **OOPIFs (Out-of-Process Iframes)**.

- Cross-site iframes are automatically spawned in a separate renderer process.
- The browser process acts as a "Compositor" that stitches together the pixel buffers from different processes into a single viewport.

---

## 9. Security & Sandboxing (`src/sandbox/`, `src/security/`)

Content in `fastrender` is treated as untrusted and runs under strict OS-level lockdowns.

### Linux Hardening (`sandbox/linux_seccomp.rs`, `sandbox/linux_landlock.rs`)

- **Seccomp-BPF**: Filters system calls to the bare minimum required for rendering (e.g., forbidding `execve`, `socket`, or direct filesystem access).
- **Landlock**: A newer Linux security module used to restrict the renderer to only seeing its own allocated temporary directories and shared memory.
- **Namespaces**: Uses `CLONE_NEWPID` and `CLONE_NEWNET` to isolate the process's view of the system.

### Windows & macOS Isolation

- **Windows (`sandbox/windows.rs`)**: Uses **AppContainer** and **Job Objects** to strip all privileges, including network access, from renderer processes.
- **macOS (`sandbox/macos.rs`)**: Implements strict **Sandbox.kext** (Seatbelt) profiles to prevent access to sensitive user data and OS APIs.

### Site Key Management (`src/security/`)

- Tracks the "Security Origin" of every frame to enforce the **Same-Origin Policy (SOP)** at the IPC level, preventing a compromised renderer from requesting data belonging to another site.

---

## 10. The Streaming & Speculative Parser (`src/html/`)

Parsing in `fastrender` is not a simple linear process; it is a high-performance, multi-threaded pipeline.

### The Preload Scanner (`html/asset_discovery.rs`)

While the main parser is blocked by a script (e.g., `<script src="...">`), a background "Preload Scanner" scans the remaining HTML for images, styles, and other scripts. This allows the network process to start fetching these resources much earlier, significantly improving page load times.

### Pausable Parsing (`html/pausable_html5ever.rs`)

To support the legacy (but still required) `document.write()` API, the parser must be able to halt mid-token, execute a script, and then resume parsing from the newly injected text. `fastrender` wraps `html5ever` with a custom pausable state machine to handle this.

---

## 11. The Heartbeat: The HTML Event Loop (`src/js/event_loop.rs`)

At **193,000 lines**, `event_loop.rs` is one of the project's most critical files. It ensures the engine follows the complex rules of the HTML spec's event loop:

- **Task Queues**: Manages multiple queues (Microtasks, UI tasks, Network tasks, Timer tasks).
- **Processing Model**: Implements the exact sequence for picking a task, running it, performing microtask checkpoints, and deciding when to "Update the Rendering."
- **Timers (`js/time.rs`)**: Implements `setTimeout`/`setInterval` with high-precision scheduling and spec-compliant clamping (e.g., the 4ms clamp for nested timers).

---

## 12. Script Lifecycle & Integration (`src/js/html_script_scheduler.rs`)

`fastrender` handles the complex orchestration of JavaScript execution:

- **Async & Defer**: Tracks the loading state of all scripts and ensures they execute in the correct order (e.g., `defer` scripts running after parsing but before `DOMContentLoaded`).
- **Module Support**: Implements the full ES Module loading graph, handled in `js/realm_module_loader.rs`.
- **Content Security Policy (CSP)**: Integrated directly into the script loader via `html/content_security_policy.rs` to block unauthorized script execution.

---

## 13. WebIDL & Automated Bindings (`src/webidl/`, `js/dom2_bindings.rs`)

To expose thousands of Rust functions to JavaScript without writing every line by hand:

- **WebIDL Generated Bindings**: Uses a custom codegen (`xtask/src/webidl_codegen.rs`) that parses WHATWG IDL files and generates specialized Rust glue code.
- **V8/VM Bridge**: Connects the generic `dom2` nodes to whichever JavaScript VM is being used, ensuring high-performance access to attributes like `className` or `children`.

---

## 14. The AV Pipeline & Synchronization (`src/media/`)

`fastrender` does not just "embed" video; it implements a full, low-latency audio/video stack.

### The Master Clock (`media/master_clock.rs`, `media/av_sync.rs`)

To prevent audio drift or "jittery" video, the engine uses a Master Clock. It monitors the hardware audio buffer's progress and uses it as the "Source of Truth" to decide exactly when to swap the next decoded video frame into the display list.

### Demuxing & Decoding (`media/demuxer.rs`, `media/decoder.rs`)

- **Containers**: Supports MP4 and WebM via `mp4.rs`.
- **Packet Management**: Buffers raw packets into a high-performance ring buffer to ensure smooth playback even under high CPU or network load.
- **YUV to RGB**: Fast, SIMD-accelerated conversion of color spaces from the video decoder (YUV) to the renderer's RGB format.

---

## 15. Vector Graphics & SVG (`src/svg.rs`)

SVG support in `fastrender` is designed for memory efficiency:

- **Lightweight Extraction**: Uses `roxmltree` to extract `viewBox` and dimensions without building a full heavy DOM tree for simple icons.
- **Coordinate Mapping**: Implements the `preserveAspectRatio` algorithm (`map_svg_aspect_ratio`) to correctly fit vector content into any destination rectangle.
- **Source Range Patching**: Uniquely handles SVGs with Illustrator-style doctypes by replacing them with spaces during parse to keep byte-offsets compatible with the original source.

---

## 16. The MathML Engine (`src/math/`)

At **224,000 lines**, the `math/mod.rs` file is a powerhouse of mathematical typesetting. It implements the MathML Core standard:

- **Box-on-Box Layout**: Unlike standard CSS, MathML requires specialized "stretchy" operators (like big integrals or brackets) and precise positioning for superscripts/subscripts.
- **Operator Dictionary**: Contains a massive `operator_dict.rs` that defines the default spacing and "stretchiness" for thousands of mathematical symbols.
- **OpenType Math Support**: Directly integrates with the MATH table in professional fonts (like STIX or Cambria Math) to extract the constants needed for perfect fractional line thickness and radical positioning.

---

## 17. The Interaction Engine (`src/interaction/engine.rs`)

At **499,000 lines**, `interaction/engine.rs` is the largest file in the entire repository. It is the brain for all user-driven updates.

### Unified Input Handling

The engine abstracts Mouse, Keyboard, Touch, and Pen into a unified event stream.

- **Hit Testing (`interaction/hit_test.rs`)**: To find what the user clicked, the engine performs a reverse walk of the Stacking Context tree. It must account for `pointer-events: none`, `z-index`, and complex CSS transforms that might have moved an element's visual position away from its logical one.
- **Focus Management**: Implements the complex "Focus Navigation" rules, handling Tab index, `autofocus`, and the `:focus-visible` pseudo-class.

### Form Submission (`interaction/form_submit.rs`)

Manages the lifecycle of `<form>` tags:

- Validates the form before submission.
- Constructs the payload (`application/x-www-form-urlencoded` or `multipart/form-data`).
- Coordinates with the network process to perform the actual navigation.

---

## 18. Animations & Transitions (`src/animation/mod.rs`)

At **412,000 lines**, the animation subsystem is a high-performance engine designed for 60fps (or 120fps) smoothness.

### The Animation Tick

The engine uses a global `tick_schedule.rs` to coordinate with the display's refresh rate (V-Sync).

- **Interpolation**: For every "ticked" frame, the engine calculates the intermediate value for every animating property.
- **GPU Path**: "Transform" and "Opacity" animations are tagged as "GPU-accelerated" and pushed directly to the renderer's compositor to avoid re-triggering expensive layout or paint.
- **Timing Functions (`animation/timing.rs`)**: Implements precise cubic-bezier easing to match the CSS `transition-timing-function` spec.

---

## 19. Scroll Handling & Anchoring (`src/scroll/`)

Scrolling in `fastrender` is more than just moving a viewport.

### Scroll Anchoring (`scroll/anchoring.rs`)

To prevent the "Jumping Page" problem (where the page shifts as images load above the current view), the engine tracks a "priority candidate" node. If layout shifts occur, the engine automatically adjusts the scroll offset to keep that node fixed relative to the viewport.

---

## 20. Accessibility (`src/accessibility/`)

`fastrender` is designed to be accessible from the ground up:

- **AccessKit Bridge**: Maps the internal semantic structure of the page (headings, buttons, text) into the **AccessKit** cross-platform format. This allows the engine to talk to screen readers like TalkBack (Android), VoiceOver (iOS/macOS), and NVDA/JAWS (Windows).
- **A11y Tree Consistency**: Uses `accesskit_tree.rs` to ensure the accessibility tree stays in sync with DOM and Layout mutations even during rapid JS-driven updates.
