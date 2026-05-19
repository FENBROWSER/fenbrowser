using FenBrowser.Js.Ast;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ParserTests
{
    [Fact]
    public void ParseScriptBuildsBinaryExpressionWithPrecedence()
    {
        var program = JsParser.ParseScript(new SourceText("1 + 2 * 3;"));

        Assert.Equal(ProgramKind.Script, program.Kind);
        var stmt = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var add = Assert.IsType<BinaryExpressionNode>(stmt.Expression);
        Assert.Equal("+", add.Operator);
        Assert.IsType<NumericLiteralExpressionNode>(add.Left);

        var mul = Assert.IsType<BinaryExpressionNode>(add.Right);
        Assert.Equal("*", mul.Operator);
    }

    [Fact]
    public void ParseModuleBuildsModuleProgramNode()
    {
        var program = JsParser.ParseModule(new SourceText("a + b"));

        Assert.Equal(ProgramKind.Module, program.Kind);
        Assert.Single(program.Body);
    }

    [Fact]
    public void ParenthesizedExpressionOverridesPrecedence()
    {
        var program = JsParser.ParseScript(new SourceText("(1 + 2) * 3"));

        var stmt = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var mul = Assert.IsType<BinaryExpressionNode>(stmt.Expression);
        Assert.Equal("*", mul.Operator);
        Assert.IsType<ParenthesizedExpressionNode>(mul.Left);
    }

    [Fact]
    public void ClassDeclarationIsReportedAsParserOnlyUnsupportedFeature()
    {
        var ex = Assert.Throws<UnsupportedFeatureException>(() => JsParser.ParseScript(new SourceText("class A {}")));
        Assert.Equal("class", ex.FeatureName);
    }
}
