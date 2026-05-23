using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record StatementNode(SourceSpan Span) : AstNode(Span);

public sealed record ExpressionStatementNode(ExpressionNode Expression, SourceSpan Span) : StatementNode(Span);

public sealed record EmptyStatementNode(SourceSpan Span) : StatementNode(Span);

public sealed record LabeledStatementNode(string Label, StatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record BlockStatementNode(IReadOnlyList<StatementNode> Statements, SourceSpan Span) : StatementNode(Span);

public sealed record VariableDeclaratorNode(string Identifier, ExpressionNode? Initializer, SourceSpan Span);

public sealed record VariableDeclarationStatementNode(string Kind, IReadOnlyList<VariableDeclaratorNode> Declarators, SourceSpan Span) : StatementNode(Span);

public sealed record IfStatementNode(ExpressionNode Test, StatementNode Consequent, StatementNode? Alternate, SourceSpan Span) : StatementNode(Span);

public sealed record WhileStatementNode(ExpressionNode Test, StatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record WithStatementNode(ExpressionNode Object, StatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record ReturnStatementNode(ExpressionNode? Argument, SourceSpan Span) : StatementNode(Span);

public sealed record FunctionDeclarationNode(string Name, IReadOnlyList<string> Parameters, BlockStatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record ThrowStatementNode(ExpressionNode Argument, SourceSpan Span) : StatementNode(Span);

public sealed record TryCatchStatementNode(
    BlockStatementNode TryBlock,
    string CatchIdentifier,
    BlockStatementNode CatchBlock,
    SourceSpan Span) : StatementNode(Span);

public sealed record TryFinallyStatementNode(
    BlockStatementNode TryBlock,
    BlockStatementNode FinallyBlock,
    SourceSpan Span) : StatementNode(Span);

public sealed record TryCatchFinallyStatementNode(
    BlockStatementNode TryBlock,
    string CatchIdentifier,
    BlockStatementNode CatchBlock,
    BlockStatementNode FinallyBlock,
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

public sealed record ForInStatementNode(
    StatementNode Initializer,
    ExpressionNode Iterable,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record BreakStatementNode(SourceSpan Span) : StatementNode(Span);

public sealed record ContinueStatementNode(SourceSpan Span) : StatementNode(Span);

public sealed record ClassDeclarationNode(
    string Name,
    ExpressionNode? BaseClass,
    IReadOnlyList<ClassMemberNode> Members,
    SourceSpan Span) : StatementNode(Span);

// One element of a class body. Kind selects the role; method bodies are stored as
// a FunctionExpressionNode so the same bytecode-compilation path the parser uses
// for `function foo() {}` applies here unchanged.
public sealed record ClassMemberNode(
    string Name,
    ClassMemberKind Kind,
    bool IsStatic,
    ExpressionNode Function,
    SourceSpan Span);

public enum ClassMemberKind : byte
{
    Constructor,
    Method,
    Getter,
    Setter,
}

public sealed record SwitchCaseNode(
    ExpressionNode? Test,
    IReadOnlyList<StatementNode> Consequent,
    SourceSpan Span);

public sealed record SwitchStatementNode(
    ExpressionNode Discriminant,
    IReadOnlyList<SwitchCaseNode> Cases,
    SourceSpan Span) : StatementNode(Span);
