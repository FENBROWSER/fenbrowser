namespace FenBrowser.Js.Host;

// Outcome of HostObjectTable.Resolve. IsOk distinguishes a valid lookup from an
// invalid one (stale generation, wrong realm, navigated-away document, dead slot,
// etc.). The reason string is intended for diagnostics / logging; production code
// should branch on IsOk rather than parse the reason.
public readonly record struct HostObjectResolution(
    bool IsOk,
    string? Reason,
    HostObjectEntry Entry,
    object? HostObject)
{
    public static HostObjectResolution Ok(HostObjectEntry entry, object? hostObject)
        => new(true, null, entry, hostObject);

    public static HostObjectResolution Invalid(string reason)
        => new(false, reason, default, null);
}
