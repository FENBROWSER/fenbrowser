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

// ECMA-262 §14.7.2 — do Statement while ( Expression );
public sealed record DoWhileStatementNode(
    BlockStatementNode Body,
    ExpressionNode Test,
    SourceSpan Span) : StatementNode(Span);

public sealed record WithStatementNode(ExpressionNode Object, StatementNode Body, SourceSpan Span) : StatementNode(Span);

public sealed record ReturnStatementNode(ExpressionNode? Argument, SourceSpan Span) : StatementNode(Span);

public sealed record FunctionDeclarationNode(
    string Name,
    IReadOnlyList<string> Parameters,
    BlockStatementNode Body,
    SourceSpan Span,
    bool IsAsync = false,
    bool IsGenerator = false,
    int RestParameterIndex = -1) : StatementNode(Span);

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

public sealed record BreakStatementNode(string? Label, SourceSpan Span) : StatementNode(Span);

public sealed record ContinueStatementNode(string? Label, SourceSpan Span) : StatementNode(Span);

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
    SourceSpan Span,
    bool IsPrivate = false,
    bool IsAsync = false,
    bool IsGenerator = false,
    ExpressionNode? ComputedName = null);

public enum ClassMemberKind : byte
{
    Constructor,
    Method,
    Getter,
    Setter,
    Field,
    StaticBlock,
}

// E.7 - top-level `import ... from "mod"` declaration. Entries holds one
// FenBrowser.Js.Modules.ImportEntry per import binding (including the
// `*namespace*` / `default` sentinel cases). Side-effect-only imports
// (`import "mod"`) carry Entries=[] and just the ModuleRequest.
public sealed record ImportDeclarationNode(
    string ModuleRequest,
    IReadOnlyList<FenBrowser.Js.Modules.ImportEntry> Entries,
    SourceSpan Span) : StatementNode(Span);

// E.7 - top-level `export ...` declaration. Entries lists the
// FenBrowser.Js.Modules.ExportEntry records produced; LocalDeclaration
// is non-null when the export wraps an inline VarDecl/FunctionDecl/
// ClassDecl ('export var x', 'export function f', 'export class C'),
// in which case the underlying declaration must be compiled too.
public sealed record ExportDeclarationNode(
    IReadOnlyList<FenBrowser.Js.Modules.ExportEntry> Entries,
    StatementNode? LocalDeclaration,
    ExpressionNode? DefaultExpression,
    SourceSpan Span) : StatementNode(Span);

public sealed record SwitchCaseNode(
    ExpressionNode? Test,
    IReadOnlyList<StatementNode> Consequent,
    SourceSpan Span);

public sealed record SwitchStatementNode(
    ExpressionNode Discriminant,
    IReadOnlyList<SwitchCaseNode> Cases,
    SourceSpan Span) : StatementNode(Span);
