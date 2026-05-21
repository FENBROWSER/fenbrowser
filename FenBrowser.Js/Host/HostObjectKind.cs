namespace FenBrowser.Js.Host;

// Classifies the renderer-owned object that a HostObjectHandle points to. The kind is
// recorded at registration time so the engine can reject mis-typed access (e.g. a
// HostObjectHandle that was issued for a DOM Element being passed where a Window is
// expected) without dereferencing the underlying browser pointer.
//
// Keep this enum closed-set and append-only - kinds are part of the engine's
// security contract with the host. Unknown values are intentionally not allowed.
public enum HostObjectKind : byte
{
    Other,
    DomNode,
    DomElement,
    DomDocument,
    DomWindow,
    DomEventTarget,
    DomEvent,
    NetworkRequest,
    NetworkResponse,
    StorageArea,
    Function,
    NamespaceObject,
}
