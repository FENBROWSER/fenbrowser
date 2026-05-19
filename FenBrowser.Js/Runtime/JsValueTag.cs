namespace FenBrowser.Js.Runtime;

public enum JsValueTag : byte
{
    Undefined,
    Null,
    Boolean,
    Int32,
    Number,
    String,
    Symbol,
    BigInt,
    Object,
    HostObject
}
