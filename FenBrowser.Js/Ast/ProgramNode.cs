using FenBrowser.Js.Source;

namespace FenBrowser.Js.Ast;

public enum ProgramKind
{
    Script,
    Module
}

public sealed record ProgramNode(ProgramKind Kind, IReadOnlyList<StatementNode> Body, SourceSpan Span) : AstNode(Span);
