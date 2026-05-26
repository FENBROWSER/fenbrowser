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
