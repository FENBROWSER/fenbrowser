namespace FenBrowser.Js.Lexer;

public enum TokenKind
{
    EndOfFile,
    Identifier,
    Keyword,
    Number,
    BigInt,
    String,
    Punctuator,
    PrivateIdentifier,
    RegularExpression,
    Template,
    Unknown
}
