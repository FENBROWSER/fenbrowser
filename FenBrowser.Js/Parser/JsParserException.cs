namespace FenBrowser.Js.Parser;

public sealed class JsParserException : Exception
{
    public JsParserException(string message) : base(message)
    {
    }

    /// <summary>
    /// 1-based line and column of the token the parser stopped at, or 0 when
    /// unknown. A host reports these with the SyntaxError (HTML "report an
    /// exception" passes them to window.onerror).
    /// </summary>
    public int Line { get; internal set; }

    public int Column { get; internal set; }
}
