namespace FenBrowser.Js.Objects;

public sealed class SymbolObject : JsObject
{
    public SymbolObject(long symbolId)
    {
        SymbolId = symbolId;
    }

    public long SymbolId { get; }
}
