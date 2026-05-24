# Web IDL Binding Contract — FenJS ↔ FenBrowser

Defines the interface contract between the standalone FenJS ECMAScript engine
and the FenBrowser host environment. The JS engine never directly knows about
DOM nodes, layout objects, or Skia surfaces. All host interaction flows through
explicit validated handles.

## 1. Brand Checks

Every host object passed to JS carries a host-object handle validated before
dereference:

- `HostObjectHandle.Index` — slot index in the host object table
- `HostObjectHandle.Generation` — prevents stale-handle reuse
- `HostObjectHandle.RealmId` — cross-realm access is rejected
- `HostObjectHandle.DocumentEpoch` — navigated-away checks

Invalid handles surface as JS TypeError before the embedder pointer is touched.

## 2. Nullable Conversion

Web IDL `?` types map to `JsValue.Undefined` when null. The engine's
`JsValueTag.Null` and `JsValueTag.Undefined` are distinct; host bindings
must handle both.

## 3. Optional Arguments

Web IDL `optional` arguments default to `undefined` when absent. The host
binding reads `args.Count > N ? args[N] : JsValue.Undefined` and applies
the IDL default.

## 4. Overload Resolution

Not yet implemented. Current bindings use exact-arity dispatch. Overload
resolution (distinguishing `(DOMString)` from `(long)`) is deferred until
a generated binding layer exists.

## 5. This-Value Validation

Host method dispatch validates the receiver via `IHostHooks.TryGetHostProperty`
/ `TrySetHostProperty`. Non-host receivers throw TypeError per Web IDL §3.7.

## 6. Same-Object Caching

Wrapper objects (e.g., `new String(hostValue)`) are not cached. Each
conversion creates a fresh wrapper. Caching is deferred until wrapper
identity becomes a conformance requirement.

## 7. Wrapper Identity

When a host object is returned to JS multiple times, the same handle is
returned (identity-stable via `HostObjectTable`). Distinct host objects
always produce distinct handles.

## 8. Realm Ownership

Each realm owns a `HostObjectTable`. Handles from realm A are invalid in
realm B. Cross-realm access raises TypeError before any embedder pointer
is dereferenced.

## 9. Exception Mapping

| Host Error | JS Exception |
|------------|-------------|
| Invalid handle (stale/epoch/cross-realm) | TypeError |
| Navigation-away during host call | TypeError |
| Host callback throws | Propagated as-is |
| Host property not found | undefined (not thrown) |
| Host setter refuses write | TypeError |

## 10. Promise Conversion

Host APIs returning `Promise<T>` use `JsPromiseObject`. The engine's
`JobQueue` and microtask checkpoint drain host-enqueued promise jobs
alongside JS-enqueued ones. `IHostPromiseRejectionTracker` surfaces
unhandled rejections to the host.

## 11. Sequence Conversion

Web IDL `sequence<T>` converts from JS iterables via the engine's
`CreateForOfIterator` pathway. Array, Set, and custom iterables are
all supported. Conversion stops at the first non-`T` value.

## 12. Dictionary Conversion

Not yet implemented. Dictionary members will be read from the JS object
via `TryGetPropertyValue` with member-name keys and converted per-member
type rules.

## 13. Cross-Origin Checks

Deferred. When implemented, `DocumentEpoch` and origin fields on
`HostObjectHandle` will gate cross-origin access at the binding layer.

## Manual Bindings (First Wave)

These APIs are bound manually to validate the contract:

- `console.log` — variadic, ToString coercion
- `queueMicrotask` — callback enqueue
- `setTimeout` / `clearTimeout` — timer host API
- `EventTarget` / `addEventListener` / `removeEventListener` / `dispatchEvent`
- `document.getElementById` / `document.querySelector`
- `Element.setAttribute` / `Element.getAttribute` / `Element.textContent`

## Generated Bindings (Future)

When the binding generator is ready, these API surfaces will be generated
from Web IDL definitions:

- `Node`, `Element`, `Document`, `Window`
- `HTMLInputElement`, `HTMLFormElement`
- `Fetch`, `Storage`, `History`, `Location`, `URL`, `Canvas`

## Engine Commit & Test262

Bound per the engine's test262-first contract. Every host-exposed API must
answer: which test262 files are enabled, which are unsupported, which
failures are expected, and which are regressions.
