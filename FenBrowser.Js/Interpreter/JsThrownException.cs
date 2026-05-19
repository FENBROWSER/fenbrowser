using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class JsThrownException : Exception
{
    public JsThrownException(JsValue value)
    {
        Value = value;
    }

    public JsValue Value { get; }
}
