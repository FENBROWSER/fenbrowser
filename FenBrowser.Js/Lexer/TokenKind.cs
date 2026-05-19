namespace FenBrowser.Js.Lexer;

public enum TokenKind
{
    EndOfFile,
    Identifier,
    Keyword,
    Number,
    String,
    Punctuator,
    PrivateIdentifier,
    RegularExpression,
    Template,
    Unknown
}
