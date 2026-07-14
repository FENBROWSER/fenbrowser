# FenBrowser Native Interop Model

Status: RESEARCHED. Snapshot date: 2026-07-14.

## Current native surface

| Surface | Managed package/API | Owning layer | Status |
| --- | --- | --- | --- |
| 2D graphics/raster | SkiaSharp 4.148.0 | Core/FenEngine/Host adapters | INTEGRATED |
| Text shaping | HarfBuzzSharp and SkiaSharp.HarfBuzz | FenEngine typography | INTEGRATED |
| SVG | Svg.Skia | FenEngine resource/render path | INTEGRATED |
| Window/input/OpenGL ES | Silk.NET Windowing/Input/OpenGLES and ANGLE native assets | Host | INTEGRATED |
| Windows window/clipboard APIs | `user32.dll` and `kernel32.dll` P/Invoke | Host | IMPLEMENTED |
| Rich text | Topten.RichTextKit | Host/adapter surface | IMPLEMENTED |

No new native language component is proposed by this audit.

## Ownership rules

1. Every native-backed object has one documented managed owner.
2. Short-lived Skia/HarfBuzz objects use `using` or a `try/finally`-equivalent deterministic release.
3. Long-lived caches implement `IDisposable`, define eviction disposal, and are released at document/renderer/host teardown.
4. Borrowed objects are never disposed by the borrower; ownership transfer is explicit in method naming or contract.
5. A native handle is never kept alive solely by an unmanaged pointer with no managed lifetime root.
6. Pinned managed memory is avoided. Any required pin has a bounded size and duration and cannot cross an asynchronous/process boundary.
7. Native exceptions/status failures are converted at the adapter boundary and traced without leaking secrets.
8. Drawing and font libraries supply measurements/raster services; they do not define CSS layout semantics.

## Thread and process rules

- Window, widget, clipboard, and OS event work stays on the UI/Host thread.
- Box Tree/layout/Paint Tree preparation stays on the engine-owned execution path.
- GPU/native resources are used only on their creating/owning context unless the API explicitly permits transfer.
- Shared frame buffers have size/format/stride limits and a process-session generation.
- Renderer teardown and GPU-process exit invalidate all related native and shared-memory handles.

## Hostile input policy

Images, fonts, SVG, shaders, and media are attacker-controlled. Before an isolated decoder exists, in-process decode requires byte, dimension, pixel-count, recursion, allocation, and time ceilings plus explicit failure logs. The target image-worker boundary accepts bytes, not a path or unrestricted stream, and returns a bounded decoded-surface handle.

## Audit and acceptance

| Work | Status | Evidence required |
| --- | --- | --- |
| Inventory native-backed types and owners | RESEARCHED | Machine-readable owner/disposal ledger |
| Audit caches and teardown for deterministic disposal | NOT_STARTED | Stress test plus stable native memory/handle counts |
| Audit P/Invoke declarations and platform guards | NOT_STARTED | Windows tests and non-Windows compile boundary |
| Add hostile image/font/SVG resource ceilings | NOT_STARTED | Negative fixtures, logs, memory limits |
| Design image decoder worker | NOT_STARTED | Boundary/IPC/memory/security ADR |
| Validate GPU/shared-memory crash cleanup | NOT_STARTED | Brokered crash/restart integration test |

Any Rust, C++, or additional native component requires a separate boundary document covering rationale, ABI or IPC, ownership, build/debug strategy, tests, fallback, and measurable success.
