using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record ExpressionNode(SourceSpan Span) : AstNode(Span);

public sealed record IdentifierExpressionNode(string Name, SourceSpan Span) : ExpressionNode(Span);

public sealed record NumericLiteralExpressionNode(double Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record StringLiteralExpressionNode(string Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record ParenthesizedExpressionNode(ExpressionNode Expression, SourceSpan Span) : ExpressionNode(Span);

public sealed record BinaryExpressionNode(string Operator, ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

public sealed record AssignmentExpressionNode(ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

public sealed record CallExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record ArrowFunctionExpressionNode(IReadOnlyList<string> Parameters, BlockStatementNode? BlockBody, ExpressionNode? ExpressionBody, SourceSpan Span) : ExpressionNode(Span);
