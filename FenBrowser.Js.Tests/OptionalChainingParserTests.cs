using FenBrowser.Js.Ast;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class OptionalChainingParserTests
{
    [Fact]
    public void ParsesOptionalMemberExpression()
    {
        var program = JsParser.ParseScript(new SourceText("a?.b;"));
        var expr = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        Assert.IsType<OptionalMemberExpressionNode>(expr.Expression);
    }

    [Fact]
    public void QuestionDotFollowedByDigitIsConditionalNotOptionalChain()
    {
        // ECMA-262 12.7: OptionalChainingPunctuator `?.` has lookahead
        // [∉ DecimalDigit], so `a?.1:.2` is a ConditionalExpression whose
        // consequent is the numeric literal `.1`, not an optional chain.
        var program = JsParser.ParseScript(new SourceText("var x=a?.1:.2;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(program.Body));
        var init = Assert.Single(decl.Declarators).Initializer;
        var conditional = Assert.IsType<ConditionalExpressionNode>(init);
        var consequent = Assert.IsType<NumericLiteralExpressionNode>(conditional.Consequent);
        Assert.Equal(0.1, consequent.Value, 10);
        var alternate = Assert.IsType<NumericLiteralExpressionNode>(conditional.Alternate);
        Assert.Equal(0.2, alternate.Value, 10);
    }

    [Fact]
    public void ParsesOptionalCallExpression()
    {
        var program = JsParser.ParseScript(new SourceText("a?.(1);"));
        var expr = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        Assert.IsType<OptionalCallExpressionNode>(expr.Expression);
    }

    [Fact]
    public void ParsesOptionalMemberThenCall()
    {
        var program = JsParser.ParseScript(new SourceText("a?.b();"));
        var expr = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var call = Assert.IsType<CallExpressionNode>(expr.Expression);
        Assert.IsType<OptionalMemberExpressionNode>(call.Callee);
    }
}
