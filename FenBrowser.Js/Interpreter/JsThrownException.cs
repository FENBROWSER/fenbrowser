using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class JsThrownException : Exception
{
    public JsThrownException(JsValue value)
    {
        Value = value;
    }

    public JsValue Value { get; }

    // Optional human-readable "Name: message" rendering of the thrown value, populated
    // by the catch site that still has the originating interpreter (and its heap) in
    // scope. Diagnostic only — null when not captured.
    public string? Description { get; set; }
}
