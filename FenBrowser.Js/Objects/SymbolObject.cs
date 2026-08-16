using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public sealed class SymbolObject : JsObject
{
    public SymbolObject(JsValue symbolValue)
    {
        if (symbolValue.Tag != JsValueTag.Symbol)
            throw new ArgumentException("SymbolObject requires a Symbol primitive.", nameof(symbolValue));

        SymbolValue = symbolValue;
    }

    public JsValue SymbolValue { get; }
    public long SymbolId => SymbolValue.AsSymbolId();
}
