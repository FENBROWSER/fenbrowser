# Technical Gap Analysis v2: `fastrender` vs. `fenbrowser`

This document expands upon previous analysis by incorporating insights from the _fastrender Exhaustive Technical Encyclopedia_. It highlights deep architectural discrepancies across the entire browser stack.

## 1. High-Level Pipeline Comparison

| Phase            | `fastrender` Implementation                                    | `fenbrowser` Implementation                              | Gap Level  |
| :--------------- | :------------------------------------------------------------- | :------------------------------------------------------- | :--------- |
| **Box Tree**     | Handles "Anonymous Box Fixup" (wrapping loose text in blocks). | Basic direct-to-layout conversion.                       | **Medium** |
| **Display List** | 23,000+ line builder following CSS Paint Order (Appendix E).   | Managed list of paint commands with basic stacking.      | **High**   |
| **Rasterize**    | Parallel tiling with `conservative_tile_halo_px` calculation.  | Direct Skia-based rendering (single surface or layered). | **High**   |

---

## 2. Layout Contexts (The Solver Depth)

| Feature          | `fastrender` Capability                                             | `fenbrowser` Status                                        | Gap Level   |
| :--------------- | :------------------------------------------------------------------ | :--------------------------------------------------------- | :---------- |
| **BFC Floats**   | Float intrusion with dynamic shortening of line boxes per Y-offset. | Basic exclusion logic (though CSS Shapes are supported).   | **Low**     |
| **IFC Struts**   | Implements the "strut" for minimum line height enforcement.         | Standard line-height calculation (no explicit strut node). | **Medium**  |
| **Table Layout** | Level 3 spec-compliant weighted scaling.                            | Standard multi-pass block distribution.                    | **Extreme** |
| **MathML**       | 224,000 lines of specialized mathematical typesetting.              | Minimal to None.                                           | **Extreme** |

---

## 3. Style & Performance Moats

- **Ancestor Bloom Filters**: `fastrender` uses hashes to "fast-reject" selectors like `div > p`. `fenbrowser` uses standard sequential matching.
- **Deadlines System**: `fastrender` has a global heartbeat (`active_deadline()`) in tight loops (Layout/Cascade) to prevent hangs. `fenbrowser` tracks budget but cannot preempt a running pass.
- **Cache Sharding**: `fastrender` uses 64-shard lock-free caches for all global metadata. `fenbrowser` uses managed dictionaries (typically single-locked).

---

## 4. Parser & Networking Robustness

- **Pausable Parsing**: `fastrender` can halt mid-token for `document.write()` injection. `fenbrowser` is a standard sequential tokenizer.
- **Preload Scanner**: Background `asset_discovery` during script blocks. `fenbrowser` fetches resources sequentially.
- **MIME Sniffing**: Full WHATWG standard implementation in `fastrender`'s resource fetcher.

---

## 5. Security & Isolation

- **Site Isolation (OOPIF)**: `fastrender` spawns separate processes for cross-origin iframes. `fenbrowser` is single-process.
- **OS Hardening**: `fastrender` implements Seccomp-BPF (Linux) and AppContainer (Windows) to strip all system privileges.

---

## 6. Multimedia & Aesthetics

- **Master Clock**: Audio-driven A/V synchronization to prevent jitter.
- **Perceptual Color**: Native Oklab/Oklch gamut mapping for gradients (avoiding sRGB's "gray dead zones").

> [!NOTE]
> While `fenbrowser` demonstrates modern features like **CSS Shapes** support, it lacks the **industrial-grade speculation** (Preload Scanner) and **perceptual precision** (Oklab) that define the `fastrender` "Exhaustive Encyclopedia" baseline.
