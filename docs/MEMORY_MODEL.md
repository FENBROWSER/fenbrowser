# FenBrowser Memory and Lifetime Model

Status: BLOCKED_NEEDS_HUMAN_DECISION. Snapshot date: 2026-07-16.

This block applies to broad WebIDL/DOM binding expansion. Existing localized fixes may continue when they preserve current ownership, but no new complex wrapper graph should be introduced until the questions below have an accepted decision record.

## Observed current model

| Object family | Current owner/root | Release behavior | Status |
| --- | --- | --- | --- |
| DOM nodes/documents | Managed Core/FenEngine object graph | Managed GC plus document/runtime teardown | INTEGRATED |
| FenJS values and objects | FenJS heap/runtime | FenJS collection rules | INTEGRATED |
| JS host handles | `HostObjectTable` slots with generation | Strong host reference until explicit `Free` | IMPLEMENTED |
| Host-object identity cache | Runtime dictionary keyed by managed object | Reset with runtime/document; strong references while active | IMPLEMENTED |
| Listener/callable/property caches | Runtime caches including conditional-weak tables | Runtime/document teardown | INTEGRATED |
| Native Skia/HarfBuzz objects | Managed native wrappers | Deterministic `Dispose`/`using` where correctly implemented | INTEGRATED |
| Shared-memory/frame handles | Process-session owners | Must be invalidated on exit/generation change | IMPLEMENTED |

`HostObjectTable` explicitly says its first version uses strong references and depends on the host calling `Free`; weak GC-aware tracking is future work. FenEngine also preserves host-object identity with a strong cache. These facts prevent an honest claim that DOM/JS cycles and wrapper lifetime are fully specified.

## Measured session behavior

The compiled `FenJsHostLifetimeMeasurementTests` characterize the active session boundary without changing ownership:

- repeated lookup of the same DOM body in one FenJS session returns the same host handle and does not grow the table;
- a fresh bound document has 6 live table entries, 6 slots, and 6 strong identity-cache entries;
- retaining 32 newly created detached elements in JavaScript raises all three counts to 38;
- each of six document/session resets replaces the interpreter/table and returns all three counts to 6;
- the two default window listeners remain stable across every reset; document listeners, pending rejection diagnostics, and WebSocket hosts return to zero;
- the adjacent stale-generation/free tests pass `7/7`, and the weak-collection host-object test passes `1/1`.

This proves current session reset bounds the active strong table and preserves same-session identity. It does not prove that an unreferenced detached node can be reclaimed within a live document, that DOM-to-JS cycles are collectible, that cross-realm identity is correct, or that old managed document graphs are collected after reset. No ownership choice follows automatically from these measurements.

## Required ownership decisions

An accepted ADR must answer all of the following:

1. Which object is authoritative for a DOM wrapper: DOM node, realm, or JS wrapper table?
2. How is one-wrapper-per-object-per-realm identity maintained?
3. Which JS roots keep DOM nodes, documents, callbacks, and listener objects alive?
4. Which DOM references keep JS callbacks alive, and when are they removed?
5. How are DOM <-> wrapper <-> listener cycles discovered and collected?
6. What semantics apply to weak references/finalization across the FenJS and .NET collectors?
7. How are detached trees, adopted nodes, cross-document wrappers, and closed realms handled?
8. What is the teardown order for timers, microtasks, observers, custom-element reactions, events, wrappers, DOM, native resources, and process handles?
9. How are stale handles rejected after runtime, document, renderer, or process restart?
10. Which pinning is permitted, with what byte/time limits and diagnostics?

## Minimum contract proposed for review

- A DOM node is owned by the managed document graph while attached or otherwise strongly referenced by managed code.
- A realm owns a wrapper identity table; the same DOM object in the same realm returns the same wrapper.
- Host handles are generation-tagged and never reused without generation change.
- Event listeners, observers, timers, and promise callbacks are explicit roots with observable registration/removal.
- Document teardown first prevents new tasks, cancels external work, drains/cancels defined queues, detaches callbacks, invalidates wrapper handles, releases native handles, and only then releases the document graph.
- Cross-process handles include a process/session generation and cannot be dereferenced after peer exit.
- No direct references cross a process boundary.

This proposal is not an accepted architecture contract.

## Required verification

- wrapper identity within a realm: TESTED for repeated lookup in one active session; across realms remains untested;
- detached-node survival while JS holds a wrapper;
- listener/observer/timer callback survival and release;
- adopted-node and navigation replacement behavior;
- repeated document/session teardown with stable live-handle counts: TESTED across six resets; renderer-process teardown remains untested;
- stale handle rejection after slot reuse and process restart;
- forced .NET and FenJS collection stress;
- native-resource and shared-memory leak checks;
- allocation/GC traces on a real SPA.

Blocker: `BLOCK-MEM-001` in `BLOCKERS.md`.
