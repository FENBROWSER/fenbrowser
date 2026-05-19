using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record StatementNode(SourceSpan Span) : AstNode(Span);

public sealed record ExpressionStatementNode(ExpressionNode Expression, SourceSpan Span) : StatementNode(Span);

public sealed record BlockStatementNode(IReadOnlyList<StatementNode> Statements, SourceSpan Span) : StatementNode(Span);

public sealed record VariableDeclaratorNode(string Identifier, ExpressionNode? Initializer, SourceSpan Span);

public sealed record VariableDeclarationStatementNode(string Kind, IReadOnlyList<VariableDeclaratorNode> Declarators, SourceSpan Span) : StatementNode(Span);

public sealed record IfStatementNode(ExpressionNode Test, StatementNode Consequent, StatementNode? Alternate, SourceSpan Span) : StatementNode(Span);

public sealed record WhileStatementNode(ExpressionNode Test, StatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record ReturnStatementNode(ExpressionNode? Argument, SourceSpan Span) : StatementNode(Span);

public sealed record FunctionDeclarationNode(string Name, IReadOnlyList<string> Parameters, BlockStatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record ThrowStatementNode(ExpressionNode Argument, SourceSpan Span) : StatementNode(Span);

public sealed record TryCatchStatementNode(
    BlockStatementNode TryBlock,
    string CatchIdentifier,
    BlockStatementNode CatchBlock,
    SourceSpan Span) : StatementNode(Span);

public sealed record ForStatementNode(
    StatementNode? Initializer,
    ExpressionNode? Test,
    ExpressionNode? Update,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record ForOfStatementNode(
    StatementNode Initializer,
    ExpressionNode Iterable,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record BreakStatementNode(SourceSpan Span) : StatementNode(Span);

public sealed record ContinueStatementNode(SourceSpan Span) : StatementNode(Span);

public sealed record ClassDeclarationNode(
    string Name,
    ExpressionNode? BaseClass,
    SourceSpan Span) : StatementNode(Span);
