using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record StatementNode(SourceSpan Span) : AstNode(Span);

public sealed record ExpressionStatementNode(ExpressionNode Expression, SourceSpan Span) : StatementNode(Span);
