namespace FenBrowser.Js.Bytecode;

// Tier 4 #20 Call IC entry. One per call-site offset in a BytecodeFunction.
// Monomorphic by design — a second distinct callee at the same site marks
// the entry Megamorphic and disables it.
internal enum CallICKind : byte
{
    None,
    Native,
    OrdinaryFunction,
}

internal sealed class CallICEntry
{
    public long CalleeHandle;
    public CallICKind Kind;
    public bool Megamorphic;
    public int Hits;
}
