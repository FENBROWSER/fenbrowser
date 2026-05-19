using FenBrowser.Js.Bytecode;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(BytecodeFunction function)
    {
        Function = function;
    }

    public BytecodeFunction Function { get; }
}
