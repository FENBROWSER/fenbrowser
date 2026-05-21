using FenBrowser.Js.Source;

namespace FenBrowser.Js.Lexer;

public readonly record struct Token(TokenKind Kind, string Text, SourceSpan Span, bool ContainsEscape = false);
