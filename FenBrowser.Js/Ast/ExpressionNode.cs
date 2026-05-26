using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record ExpressionNode(SourceSpan Span) : AstNode(Span);

public sealed record IdentifierExpressionNode(string Name, SourceSpan Span) : ExpressionNode(Span);

public sealed record ThisExpressionNode(SourceSpan Span) : ExpressionNode(Span);

// H.5 - `new.target` meta-property. Lowers to a single LoadNewTarget opcode
// reading the current frame's [[NewTarget]]. ECMA-262 13.3.12.
public sealed record NewTargetExpressionNode(SourceSpan Span) : ExpressionNode(Span);

// H.3 - the `super` keyword as an expression-position placeholder; only valid
// as the object of a MemberExpression (super.foo / super[expr]). Direct use
// raises SyntaxError at compile time. Naked super calls (super(...)) are not
// yet supported.
public sealed record SuperExpressionNode(SourceSpan Span) : ExpressionNode(Span);

public sealed record ClassExpressionNode(string? Name, ExpressionNode? BaseClass, IReadOnlyList<ClassMemberNode> Members, SourceSpan Span) : ExpressionNode(Span);

public sealed record NumericLiteralExpressionNode(double Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record BigIntLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record StringLiteralExpressionNode(string Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record BooleanLiteralExpressionNode(bool Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record NullLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record ParenthesizedExpressionNode(ExpressionNode Expression, SourceSpan Span) : ExpressionNode(Span);

public sealed record BinaryExpressionNode(string Operator, ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

public sealed record AssignmentExpressionNode(ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

public sealed record CallExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record ArrowFunctionExpressionNode(
    IReadOnlyList<string> Parameters,
    BlockStatementNode? BlockBody,
    ExpressionNode? ExpressionBody,
    SourceSpan Span,
    bool IsAsync = false,
    int RestParameterIndex = -1) : ExpressionNode(Span);

public sealed record ObjectPropertyNode(string? Key, ExpressionNode? ComputedKey, bool IsComputed, ExpressionNode Value, SourceSpan Span);

public sealed record ObjectLiteralExpressionNode(IReadOnlyList<ObjectPropertyNode> Properties, SourceSpan Span) : ExpressionNode(Span);

public sealed record ArrayLiteralExpressionNode(IReadOnlyList<ExpressionNode> Elements, SourceSpan Span) : ExpressionNode(Span);

public sealed record SpreadElementExpressionNode(ExpressionNode Argument, SourceSpan Span) : ExpressionNode(Span);

public sealed record MemberExpressionNode(ExpressionNode Object, string Property, bool Computed, ExpressionNode? PropertyExpression, SourceSpan Span) : ExpressionNode(Span);

public sealed record UnaryExpressionNode(string Operator, ExpressionNode Operand, SourceSpan Span) : ExpressionNode(Span);

public sealed record ConditionalExpressionNode(ExpressionNode Test, ExpressionNode Consequent, ExpressionNode Alternate, SourceSpan Span) : ExpressionNode(Span);

public sealed record FunctionExpressionNode(
    string? Name,
    IReadOnlyList<string> Parameters,
    BlockStatementNode Body,
    SourceSpan Span,
    bool IsAsync = false,
    bool IsGenerator = false,
    int RestParameterIndex = -1) : ExpressionNode(Span);

public sealed record NewExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record RegexLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record TemplateLiteralExpressionNode(IReadOnlyList<string> Quasis, IReadOnlyList<ExpressionNode> Expressions, SourceSpan Span) : ExpressionNode(Span);

public sealed record TaggedTemplateExpressionNode(ExpressionNode Tag, TemplateLiteralExpressionNode Template, SourceSpan Span) : ExpressionNode(Span);
