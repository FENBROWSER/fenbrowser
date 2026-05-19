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

    [Fact]
    public void ParsesVariableDeclarationsAndAssignments()
    {
        var program = JsParser.ParseScript(new SourceText("let x = 1; x = x + 1;"));
        Assert.Equal(2, program.Body.Count);
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        Assert.Equal("let", decl.Kind);
        Assert.Single(decl.Declarators);
        Assert.Equal("x", decl.Declarators[0].Identifier);

        var exprStmt = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        Assert.IsType<AssignmentExpressionNode>(exprStmt.Expression);
    }

    [Fact]
    public void ParsesIfElseAndWhileStatements()
    {
        var source = "if (x) { y = 1; } else { y = 2; } while (y) y = y - 1;";
        var program = JsParser.ParseScript(new SourceText(source));
        Assert.Equal(2, program.Body.Count);
        Assert.IsType<IfStatementNode>(program.Body[0]);
        Assert.IsType<WhileStatementNode>(program.Body[1]);
    }

    [Fact]
    public void ParsesFunctionDeclarationWithReturn()
    {
        var program = JsParser.ParseScript(new SourceText("function add(a,b){ return a + b; }"));
        var fn = Assert.IsType<FunctionDeclarationNode>(Assert.Single(program.Body));
        Assert.Equal("add", fn.Name);
        Assert.Equal(new[] { "a", "b" }, fn.Parameters);
        Assert.Single(fn.Body.Statements);
        Assert.IsType<ReturnStatementNode>(fn.Body.Statements[0]);
    }

    [Fact]
    public void ParsesArrowFunctionExpressionBody()
    {
        var program = JsParser.ParseScript(new SourceText("const inc = x => x + 1;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(program.Body));
        var arrow = Assert.IsType<ArrowFunctionExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(arrow.Parameters);
        Assert.NotNull(arrow.ExpressionBody);
    }

    [Fact]
    public void ParsesTryCatchAndThrow()
    {
        var program = JsParser.ParseScript(new SourceText("try { throw 1; } catch (e) { e; }"));
        var tc = Assert.IsType<TryCatchStatementNode>(Assert.Single(program.Body));
        Assert.Equal("e", tc.CatchIdentifier);
        Assert.Single(tc.TryBlock.Statements);
        Assert.IsType<ThrowStatementNode>(tc.TryBlock.Statements[0]);
    }

    [Fact]
    public void ParsesObjectArrayAndMemberExpressions()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { a: 1 }; let arr = [1,2]; o.a = arr[1]; o.a;"));
        Assert.Equal(4, program.Body.Count);
        var oDecl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        Assert.IsType<ObjectLiteralExpressionNode>(oDecl.Declarators[0].Initializer);
        var arrDecl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[1]);
        Assert.IsType<ArrayLiteralExpressionNode>(arrDecl.Declarators[0].Initializer);
        var assignStmt = Assert.IsType<ExpressionStatementNode>(program.Body[2]);
        Assert.IsType<AssignmentExpressionNode>(assignStmt.Expression);
    }

    [Fact]
    public void ParsesUnaryAndConditionalExpressions()
    {
        var program = JsParser.ParseScript(new SourceText("let x = !0 ? -3 : 5; x;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var cond = Assert.IsType<ConditionalExpressionNode>(decl.Declarators[0].Initializer);
        Assert.IsType<UnaryExpressionNode>(cond.Test);
        Assert.IsType<UnaryExpressionNode>(cond.Consequent);
    }
}
