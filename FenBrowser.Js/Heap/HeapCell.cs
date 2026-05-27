namespace FenBrowser.Js.Heap;

// Tier 4 #22: GC generation tier. New allocations land in Young; cells that
// survive PromotionThreshold minor collections are moved to Old. The old
// `Generation` field on this cell is unrelated — it is the per-slot reuse
// generation used for ABA-safe handle validation.
public enum GenerationTier : byte
{
    Young,
    Old,
}

public sealed class HeapCell
{
    public int Generation;

    public bool Marked;

    public required HeapCellKind Kind;

    public required ITraceable Payload;

    // Generational GC bookkeeping.
    public GenerationTier Tier;
    public byte MinorSurvivedCount;
}
