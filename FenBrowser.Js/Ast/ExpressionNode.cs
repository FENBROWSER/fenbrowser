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

// ECMA-262 13.3.10 — `import(specifier)` ImportCall. Returns a Promise that
// settles when the host's module resolver finishes loading the requested
// module. FenJS lowers this to a Promise.reject(TypeError) until a host
// module resolver is wired (Step E.6.next).
public sealed record ImportCallExpressionNode(ExpressionNode Specifier, SourceSpan Span) : ExpressionNode(Span);

// ECMA-262 13.3.12 — `import.meta`. Lowers to an empty object (the host
// metadata hook is not yet wired). Returning an object lets test262 syntax
// tests parse and execute the surrounding harness without a parser error.
public sealed record ImportMetaExpressionNode(SourceSpan Span) : ExpressionNode(Span);

// ES2025 Import Source proposal — `import.source(specifier)`. Syntactic form
// `import.source(AssignmentExpression)` that returns a rejected Promise (no
// host module resolver wired yet). `import.source` without parens is a SyntaxError.
public sealed record ImportSourceExpressionNode(ExpressionNode Specifier, SourceSpan Span) : ExpressionNode(Span);

// ES2025 Import Defer proposal — `import.defer(specifier)`. Syntactic form
// `import.defer(AssignmentExpression)` that returns a rejected Promise (no
// host module resolver wired yet). `import.defer` without parens is a SyntaxError.
public sealed record ImportDeferExpressionNode(ExpressionNode Specifier, SourceSpan Span) : ExpressionNode(Span);

public sealed record ClassExpressionNode(string? Name, ExpressionNode? BaseClass, IReadOnlyList<ClassMemberNode> Members, SourceSpan Span) : ExpressionNode(Span);

public sealed record NumericLiteralExpressionNode(double Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record BigIntLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record StringLiteralExpressionNode(string Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record BooleanLiteralExpressionNode(bool Value, string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record NullLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record ParenthesizedExpressionNode(ExpressionNode Expression, SourceSpan Span) : ExpressionNode(Span);

public sealed record BinaryExpressionNode(string Operator, ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

public sealed record AssignmentExpressionNode(ExpressionNode Left, ExpressionNode Right, SourceSpan Span) : ExpressionNode(Span);

// Logical assignment (&&=, ||=, ??=). Operator is the underlying logical
// operator ("&&", "||", "??"). Unlike a desugared `x = x op y`, these
// short-circuit: when the operator's condition is not met, the right side is
// not evaluated and no assignment (PutValue) is performed.
public sealed record LogicalAssignmentExpressionNode(ExpressionNode Target, string Operator, ExpressionNode Value, SourceSpan Span) : ExpressionNode(Span);

public sealed record CallExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record ArrowFunctionExpressionNode(
    IReadOnlyList<string> Parameters,
    BlockStatementNode? BlockBody,
    ExpressionNode? ExpressionBody,
    SourceSpan Span,
    bool IsAsync = false,
    bool HasSimpleParameterList = true,
    int RestParameterIndex = -1,
    IReadOnlyList<BindingPatternNode?>? ParameterBindings = null,
    IReadOnlyList<ExpressionNode?>? ParameterDefaults = null) : ExpressionNode(Span);

public enum ObjectPropertyKind { Data, Getter, Setter }

// IsCoverInitializedName marks the `{ key = default }` shorthand-with-default form
// (CoverInitializedName, ECMA-262 13.2.5). It is a SyntaxError in a real object literal
// but the assignment-pattern cover grammar needs to tell it apart from `{ key: value }`,
// which is otherwise structurally identical in the AST (both Key="key", Value=<expr>).
public sealed record ObjectPropertyNode(string? Key, ExpressionNode? ComputedKey, bool IsComputed, ExpressionNode Value, SourceSpan Span, ObjectPropertyKind Kind = ObjectPropertyKind.Data, bool IsCoverInitializedName = false);

// HasDuplicateProtoSetter marks an object literal that contains more than one
// `__proto__: value` colon-form data property (ECMA-262 B.3.1) — a SyntaxError
// for a real ObjectLiteral, but permitted when the same source is reinterpreted
// as an ObjectAssignmentPattern (cover grammar), so the error is deferred.
public sealed record ObjectLiteralExpressionNode(IReadOnlyList<ObjectPropertyNode> Properties, SourceSpan Span, bool HasDuplicateProtoSetter = false) : ExpressionNode(Span);

public sealed record ArrayLiteralExpressionNode(IReadOnlyList<ExpressionNode> Elements, SourceSpan Span) : ExpressionNode(Span);

// An array-literal elision (hole), e.g. the missing element in `[1, , 3]`. Distinct
// from an explicit `undefined` so the compiler can leave the index absent (a true
// hole that HasProperty/iteration methods skip) rather than storing undefined.
public sealed record ElisionExpressionNode(SourceSpan Span) : ExpressionNode(Span);

public sealed record SpreadElementExpressionNode(ExpressionNode Argument, SourceSpan Span) : ExpressionNode(Span);

public sealed record MemberExpressionNode(ExpressionNode Object, string Property, bool Computed, ExpressionNode? PropertyExpression, SourceSpan Span) : ExpressionNode(Span);

public sealed record OptionalMemberExpressionNode(ExpressionNode Object, string Property, bool Computed, ExpressionNode? PropertyExpression, SourceSpan Span) : ExpressionNode(Span);

public sealed record OptionalCallExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record UnaryExpressionNode(string Operator, ExpressionNode Operand, SourceSpan Span) : ExpressionNode(Span);

public sealed record ConditionalExpressionNode(ExpressionNode Test, ExpressionNode Consequent, ExpressionNode Alternate, SourceSpan Span) : ExpressionNode(Span);

public sealed record FunctionExpressionNode(
    string? Name,
    IReadOnlyList<string> Parameters,
    BlockStatementNode Body,
    SourceSpan Span,
    bool IsAsync = false,
    bool IsGenerator = false,
    bool HasSimpleParameterList = true,
    int RestParameterIndex = -1,
    IReadOnlyList<BindingPatternNode?>? ParameterBindings = null,
    IReadOnlyList<ExpressionNode?>? ParameterDefaults = null,
    // True for concise methods and accessors (`{ m(){} }`, `get x(){}`, class
    // methods). Per ECMA-262 these are MethodDefinitions: they have no own
    // `prototype` and no [[Construct]], unlike `{ m: function(){} }`.
    bool IsMethod = false) : ExpressionNode(Span);

public sealed record NewExpressionNode(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, SourceSpan Span) : ExpressionNode(Span);

public sealed record RegexLiteralExpressionNode(string RawText, SourceSpan Span) : ExpressionNode(Span);

public sealed record TemplateLiteralExpressionNode(IReadOnlyList<string> Quasis, IReadOnlyList<ExpressionNode> Expressions, SourceSpan Span) : ExpressionNode(Span);

public sealed record TaggedTemplateExpressionNode(ExpressionNode Tag, TemplateLiteralExpressionNode Template, SourceSpan Span) : ExpressionNode(Span);

// Synthesised by the bytecode compiler prologue when a default parameter initializer
// references a parameter that is still in the TDZ (the parameter itself or a later one).
// Lowers to a throw ReferenceError at compile time. ECMA-262 10.2.1.3 step 25.c.i.2.
public sealed record TdzReferenceErrorExpressionNode(string ParameterName, SourceSpan Span) : ExpressionNode(Span);
