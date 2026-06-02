// ECMA-262 §22.2.1 Pattern syntax errors.
// Regex-specific exception hierarchy isolated from the core JS exception path.

namespace FenBrowser.Js.Regex;

/// <summary>
/// Thrown when regex pattern or flags contain a syntax error.
/// Callers in the builtin layer catch this and convert to a JS SyntaxError.
/// </summary>
public class RegexSyntaxError : Exception
{
    public RegexSyntaxError(string message) : base(message) { }
    public RegexSyntaxError(string message, int position)
        : base($"{message} at position {position}") { }
}
