using FenBrowser.Js.Ast;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemplateSubstitutionContextTests
{
    [Fact]
    public void SubstitutionExpressionUsesAbsoluteSourceSpan()
    {
        const string sourceText = "const value = `head ${alpha + beta}`;";
        var source = new SourceText(sourceText);
        var program = JsParser.ParseScript(source);
        var declaration = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(program.Body));
        var template = Assert.IsType<TemplateLiteralExpressionNode>(Assert.Single(declaration.Declarators).Initializer);
        var expression = Assert.IsType<BinaryExpressionNode>(Assert.Single(template.Expressions));
        var expectedStart = sourceText.IndexOf("alpha", StringComparison.Ordinal);
        var (expectedLine, expectedColumn) = source.GetLineColumn(expectedStart);

        Assert.Equal(expectedStart, expression.Span.Start);
        Assert.Equal(expectedLine, expression.Span.Line);
        Assert.Equal(expectedColumn, expression.Span.Column);
    }

    [Fact]
    public void MultilineSubstitutionUsesOuterLineAndColumn()
    {
        const string sourceText = "const value = `head\n${alpha}`;";
        var source = new SourceText(sourceText);
        var program = JsParser.ParseScript(source);
        var declaration = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(program.Body));
        var template = Assert.IsType<TemplateLiteralExpressionNode>(Assert.Single(declaration.Declarators).Initializer);
        var expression = Assert.IsType<IdentifierExpressionNode>(Assert.Single(template.Expressions));
        var expectedStart = sourceText.IndexOf("alpha", StringComparison.Ordinal);
        var (expectedLine, expectedColumn) = source.GetLineColumn(expectedStart);

        Assert.Equal(new SourceSpan(expectedStart, "alpha".Length, expectedLine, expectedColumn), expression.Span);
    }

    [Fact]
    public void StaticBlockRestrictionsApplyInsideTemplateSubstitution()
    {
        Assert.Throws<JsParserException>(() =>
            JsParser.ParseScript(new SourceText("class C { static { `${await}`; } }")));
    }
}
