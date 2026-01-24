# Technical Gap Analysis: `fastrender` vs. `fenbrowser`

This document provides a comprehensive list of technical features in `fastrender` that are missing, partially implemented, or only exist in basic form in `fenbrowser`.

## 1. Architecture & Security (The "Moats")

| Feature                   | `fastrender` Capability                                                                            | `fenbrowser` Status                                                    | Gap Level   |
| :------------------------ | :------------------------------------------------------------------------------------------------- | :--------------------------------------------------------------------- | :---------- |
| **Site Isolation**        | Full OOPIF (Out-of-Process Iframes) support. Cross-site frames run in isolated renderer processes. | Single-process managed environment. No origin-based process isolation. | **Extreme** |
| **OS Sandboxing**         | Seccomp-BPF/Landlock (Linux), AppContainer (Win), Seatbelt (macOS). Strip all privileges.          | Managed C# sandboxing via permissions/policies only.                   | **High**    |
| **Intra-frame Deadlines** | `render_control` heartbeats in layout/style loops. Aborts long frames mid-calculation.             | 16ms budget warnings; no mid-pass preemption.                          | **High**    |
| **IPC Serialization**     | High-performance binary framed codecs with shared memory for pixel buffers.                        | C# object passing/managed memory.                                      | **Medium**  |

## 2. Rendering Engine (Visual Fidelity)

| Feature              | `fastrender` Capability                                                               | `fenbrowser` Status                                        | Gap Level  |
| :------------------- | :------------------------------------------------------------------------------------ | :--------------------------------------------------------- | :--------- |
| **Perceptual Color** | Native Oklab/Oklch gamut mapping for all gradients and colors. No gray zones.         | Standard sRGB (Skia defaults).                             | **High**   |
| **Tile Halo Logic**  | Calculated `conservative_tile_halo_px` to prevent seams in parallel tiled rendering.  | Continuous rasterization (no tiled halo prep).             | **High**   |
| **Composite Phase**  | Dedicated 5th phase for merging stacking context buffers on GPU.                      | 4-Stage direct-to-surface or intermediate layer rendering. | **Medium** |
| **Custom Blending**  | Manual ASM-optimized implementations for complex CSS blend modes (Color, Saturation). | Relies on Skia's built-in `SkBlendMode`.                   | **Medium** |

## 3. Layout Engine (Computational Solving)

| Feature               | `fastrender` Capability                                                               | `fenbrowser` Status                              | Gap Level   |
| :-------------------- | :------------------------------------------------------------------------------------ | :----------------------------------------------- | :---------- |
| **Table Level 3**     | Spec-compliant "Auto" distribution with weighted scaling and fixpoint height resolve. | Basic multi-pass (standard implementation).      | **Extreme** |
| **Cache Sharding**    | 64-shard lock-free intrinsic layout caches for massive multi-core scaling.            | Single-lock / Threaded caches.                   | **High**    |
| **MathML Engine**     | 224,000 lines of specialized Math typesetting and OpenType MATH table support.        | Minimal to None.                                 | **Extreme** |
| **Margin Collapsing** | Separation of positive/negative components to resolve CSS 2.1 edge cases.             | Standard Resolve (likely single-value tracking). | **Medium**  |

## 4. DOM & Scripting (State & Lifecycle)

| Feature               | `fastrender` Capability                                                          | `fenbrowser` Status                           | Gap Level  |
| :-------------------- | :------------------------------------------------------------------------------- | :-------------------------------------------- | :--------- |
| **Mutation Logs**     | First-class `MutationLog` with automatic `nodes_moved` detection (O(1) updates). | Manual ChildDirty flag propagation.           | **High**   |
| **Preload Scanner**   | Background `asset_discovery` scanner during render-blocking script execution.    | Sequential fetching (blocked by scripts).     | **High**   |
| **Shadow ID Scoping** | `get_element_by_id_from` respects strict boundary encapsulation.                 | Global-ish ID resolution (potential leakage). | **Medium** |
| **Binding Codegen**   | Xtask-driven WebIDL to Rust glue for 1000s of DOM APIs.                          | Manual/Reflected JS-to-C# bindings.           | **High**   |

## 5. Multimedia & Internationalization

| Feature              | `fastrender` Capability                                                | `fenbrowser` Status                               | Gap Level  |
| :------------------- | :--------------------------------------------------------------------- | :------------------------------------------------ | :--------- |
| **Master Clock**     | Audio-driven A/V synchronization to prevent jitter and drift.          | Standard timers/buffer-monitoring.                | **High**   |
| **Text Shaping**     | Integrated GSUB/GPOS pipeline with floating-point line-box shortening. | Decoupled `BidiResolver` (standard Skia shaping). | **Medium** |
| **YUV Acceleration** | SIMD-accelerated YUV-to-RGB conversion for video planes.               | Managed conversion or Skia-mediated.              | **Medium** |

## 6. Interaction & Accessibility

| Feature              | `fastrender` Capability                                                                 | `fenbrowser` Status                          | Gap Level |
| :------------------- | :-------------------------------------------------------------------------------------- | :------------------------------------------- | :-------- |
| **Unified Events**   | 499,000 line engine for Mouse/Touch/Pen/Keyboard abstraction.                           | Standard event handlers and input routing.   | **High**  |
| **AccessKit Bridge** | Native mapping to accessibility screen readers (TalkBack/VoiceOver).                    | Basic ARIA/DOM visibility.                   | **High**  |
| **Scroll Anchoring** | Automatic offset adjustment to keep "priority candidates" in view during layout shifts. | Visual-only scrolling (no shift-correction). | **High**  |

---

> [!NOTE]
> `fastrender`'s primary advantage is its **industrial-scale robustness** (deadlines, sharding, isolation), while `fenbrowser` is built for **clean implementation** and **managed safety**. The gaps are most significant in areas requiring deep OS integration or extreme speculation (like the Preload Scanner).
