namespace FenBrowser.Js.Host;

// A monotonically increasing counter that ticks every time a browser document
// navigates away. The renderer hands the current epoch into every HostObjectEntry at
// creation; subsequent validation rejects entries whose stored epoch is older than
// the current one. This is the lifetime-correctness primitive that prevents stale DOM
// wrappers from being resurrected after a page navigation.
//
// Modeled as a value type rather than a long-typed field so the compiler can
// distinguish "epoch number" from "any other integer" at API boundaries.
public readonly record struct NavigationEpoch(long Value)
{
    public static NavigationEpoch Initial => new(1);

    public NavigationEpoch Next() => new(Value + 1);

    public bool IsValidIn(NavigationEpoch current) => Value == current.Value;
}

// The per-document counterpart - distinct documents within the same renderer (e.g.
// iframes) carry their own epoch so that navigating one frame does not invalidate
// wrappers held by another.
public readonly record struct DocumentEpoch(long Value)
{
    public static DocumentEpoch Initial => new(1);

    public DocumentEpoch Next() => new(Value + 1);

    public bool IsValidIn(DocumentEpoch current) => Value == current.Value;
}
