using FenBrowser.Js.Bytecode;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(BytecodeFunction function, IReadOnlyDictionary<string, Runtime.JsValue>? capturedVariables = null)
    {
        Function = function;
        CapturedVariables = capturedVariables ?? new Dictionary<string, Runtime.JsValue>(StringComparer.Ordinal);
    }

    public BytecodeFunction Function { get; }

    public IReadOnlyDictionary<string, Runtime.JsValue> CapturedVariables { get; }
}
