namespace FenBrowser.Js.Host;

// Per plan §23 the HostObjectTable stores one of these per registered host object.
// Every JS-to-host access validates against this entry before touching the embedder's
// pointer.
//
// Slots:
//   Generation     : monotonic per-index counter; bumped when the slot is reused so
//                    a stale handle holding the old generation is rejected.
//   Kind           : closed-set classifier (HostObjectKind).
//   RealmId        : owning realm; cross-realm access is blocked unless the host
//                    explicitly allows it via a separate cross-realm proxy.
//   Origin         : security origin (stored as opaque string here; the renderer
//                    parses it into its Origin type).
//   DocumentEpoch  : document this entry was created in. Navigations bump the epoch
//                    and orphan every entry from the prior document.
//   NavigationEpoch: top-level navigation epoch; coarser invalidation knob.
//   FrameId        : opaque id of the owning frame; useful for diagnostics and for
//                    detecting iframe lifetime changes.
//   PermissionFlags: bit field reserved for capability gating (e.g. "this DOM node
//                    may be passed cross-origin").
//
// The actual browser pointer is held in a separate weak-reference slot inside the
// table - keeping the table row a pure value type lets the engine validate handles
// without keeping the underlying object alive.
public readonly record struct HostObjectEntry(
    int Generation,
    HostObjectKind Kind,
    int RealmId,
    string Origin,
    DocumentEpoch DocumentEpoch,
    NavigationEpoch NavigationEpoch,
    int FrameId,
    int PermissionFlags);
