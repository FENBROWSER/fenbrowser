using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public class JsThrownException : Exception
{
    public JsThrownException(JsValue value)
    {
        Value = value;
    }

    public JsValue Value { get; }

    /// <summary>
    /// An abort the script is not allowed to observe: an exhausted instruction
    /// budget, an interrupt, a wall-clock deadline. The dispatch loop raises
    /// these outside its handler routing so no try/catch can swallow the very
    /// thing stopping a runaway script, and compiled bodies must leave them
    /// alone for the same reason.
    /// </summary>
    internal bool IsUncatchableByScript { get; init; }

    // Optional human-readable "Name: message" rendering of the thrown value, populated
    // by the catch site that still has the originating interpreter (and its heap) in
    // scope. Diagnostic only — null when not captured.
    public string? Description { get; set; }

    // Surface the rendered JS error (e.g. "TypeError: x is not a function") through the
    // standard Exception.Message so host catch sites that only log ex.Message no longer
    // show the useless "Exception of type '...JsThrownException' was thrown." default.
    public override string Message =>
        string.IsNullOrEmpty(Description)
            ? "Uncaught (in JS) " + Value.Tag
            : "Uncaught (in JS) " + Description;
}
