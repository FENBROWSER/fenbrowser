using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public abstract record BindingPatternNode(SourceSpan Span) : AstNode(Span);

public sealed record IdentifierBindingPatternNode(string Name, SourceSpan Span) : BindingPatternNode(Span);

public sealed record ArrayBindingElementNode(
    BindingPatternNode? Target,
    ExpressionNode? Initializer,
    bool IsRest,
    SourceSpan Span);

public sealed record ArrayBindingPatternNode(
    IReadOnlyList<ArrayBindingElementNode> Elements,
    SourceSpan Span) : BindingPatternNode(Span);

public sealed record ObjectBindingPropertyNode(
    string? Key,
    ExpressionNode? ComputedKey,
    bool IsComputed,
    BindingPatternNode Target,
    ExpressionNode? Initializer,
    SourceSpan Span);

public sealed record ObjectBindingPatternNode(
    IReadOnlyList<ObjectBindingPropertyNode> Properties,
    BindingPatternNode? Rest,
    SourceSpan Span) : BindingPatternNode(Span);
