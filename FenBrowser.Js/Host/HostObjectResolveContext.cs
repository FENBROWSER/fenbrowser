namespace FenBrowser.Js.Host;

// The context the engine passes to HostObjectTable.Resolve to validate a handle. The
// table cross-checks every field against the slot's stored HostObjectEntry; a
// mismatch becomes an invalid handle outcome.
public readonly record struct HostObjectResolveContext(
    int CurrentRealmId,
    DocumentEpoch CurrentDocumentEpoch,
    NavigationEpoch CurrentNavigationEpoch);
