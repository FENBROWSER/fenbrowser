# FenBrowser Current Architecture

Status: RESEARCHED. Snapshot date: 2026-07-14.

This is the live architecture overlay required by the execution system. The canonical subsystem descriptions remain `VOLUME_I_SYSTEM_MANIFEST.md` through `VOLUME_VI_EXTENSIONS_VERIFICATION.md`; this file records what is active in the current checkout, what is design intent, and which changes require decisions.

## Current dependency shape

```text
FenBrowser.Host
  -> FenBrowser.DevTools
  -> FenBrowser.WebDriver
  -> FenBrowser.FenEngine
       -> FenBrowser.Core
       -> FenBrowser.Js

FenBrowser.Tooling
  -> Host/FenEngine/Core diagnostic and automation surfaces

FenBrowser.WebIdlGen
  -x- generated FenEngine bindings (generation exists; compile inclusion does not)
```

The active page path remains a C#/.NET browser stack. FenJS owns ECMAScript execution; FenEngine owns browser host integration, CSS, Box Tree, layout, Paint Tree, and raster orchestration; Host owns windows, input, tab lifecycle, process startup, and OS boundaries.

## Runtime ownership

| Capability | Current owner | Status | Boundary truth |
| --- | --- | --- | --- |
| Browser UI, tabs, window, platform input | `FenBrowser.Host` | INTEGRATED | UI-thread and platform-owned |
| Navigation/resource orchestration | `FenBrowser.Core` plus Host wiring | INTEGRATED | Active network path remains in process |
| DOM and HTML parser | `FenBrowser.Core` | INTEGRATED | Document state is renderer/page-local by design |
| JS host integration and browser APIs | `FenBrowser.FenEngine` | INTEGRATED | Manual host dispatch is active |
| ECMAScript runtime and heap | `FenBrowser.Js` | TESTED | Separate managed heap with host handles |
| CSS, Box Tree, layout, Paint Tree | `FenBrowser.FenEngine` | TESTED | Engine/render-thread work |
| Native window/GPU/clipboard integration | `FenBrowser.Host` | INTEGRATED | Platform and native-resource boundary |
| Per-tab renderer child | `FenBrowser.Host/ProcessIsolation` | IMPLEMENTED | Available only when brokered mode is selected |
| Network child | `FenBrowser.Host/ProcessIsolation/Network` | IMPLEMENTED | Coordinator is not wired into the active fetch path |
| GPU and utility targets | `FenBrowser.Host/ProcessIsolation` | INTEGRATED | Started in brokered mode; acceptance coverage is open |
| Storage service process | Unassigned | NOT_STARTED | Storage remains page/runtime local |
| Image decoder worker | Unassigned | NOT_STARTED | Decode remains in process |
| Trace collection | Core logging plus Tooling bundle writer | INTEGRATED | No cross-process-complete bundle contract yet |

## Architecture invariants

1. C#/.NET remains the primary implementation language.
2. DOM, JS-wrapper, and native-handle ownership must be explicit before broad generated-binding activation.
3. Renderer logic must not gain new direct privileged access.
4. Platform APIs remain in Host or an explicitly documented platform adapter.
5. Box/LayoutBox semantics are decided by the engine, not by Skia or a window library.
6. IPC payloads are untrusted at every receiver and require bounded validation.
7. Page behavior and diagnostic behavior are separate: trace failure must not change page execution.
8. Architecture migrations require side-by-side evidence and a decision record before changing defaults.

## Current execution topology

The default factory path is in-process when `FEN_PROCESS_ISOLATION` is absent, despite a stale summary comment saying brokered is the default. Explicit `brokered`, `auto`, `enabled`, `on`, or `1` selects child processes. Renderer, network, GPU, and utility process implementations exist, but their presence does not prove the default request/render path is isolated.

See `PROCESS_MODEL.md` for process lifecycle and `IPC_MODEL.md` for the wire audit.

## Stabilization sequence

| Sequence | Architecture action | Status | Acceptance evidence |
| --- | --- | --- | --- |
| 1 | Complete the diagnostic bundle and deterministic first blocker | INTEGRATED | Local failure fixture plus fresh real-site bundle |
| 2 | Freeze current memory and wrapper-lifetime rules | BLOCKED_NEEDS_HUMAN_DECISION | Accepted memory ADR and teardown tests |
| 3 | Version and unify typed IPC contracts | BLOCKED_NEEDS_HUMAN_DECISION | Accepted IPC ADR, validators, compatibility tests, fuzz tests |
| 4 | Put one brokered Google run through renderer IPC | DESIGNED | UI survives renderer failure; complete trace artifacts |
| 5 | Wire network brokerage without silent fallback | BLOCKED_NEEDS_HUMAN_DECISION | Same request results in lockstep mode; failure policy accepted |
| 6 | Activate one generated WebIDL family behind conformance tests | DESIGNED | Wrapper identity, conversion, brand, descriptor, and WPT reductions |
| 7 | Design storage and image-decode process boundaries | NOT_STARTED | Threat model, IPC/memory contract, recovery tests |

## Architecture decisions still open

- Default process mode and behavior when child startup fails.
- Whether network-process unavailability fails closed, degrades under an explicit development policy, or is retried.
- IPC version negotiation and migration policy.
- DOM/wrapper ownership, cycle collection, weak references, callback roots, and teardown.

These are listed in `BLOCKERS.md`. No default or public contract is changed by this audit.
