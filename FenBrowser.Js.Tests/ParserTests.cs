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

    [Fact]
    public void ParsesBasicForStatement()
    {
        var program = JsParser.ParseScript(new SourceText("for (let i = 0; i < 3; i = i + 1) { i; }"));
        var forStmt = Assert.IsType<ForStatementNode>(Assert.Single(program.Body));
        Assert.NotNull(forStmt.Initializer);
        Assert.NotNull(forStmt.Test);
        Assert.NotNull(forStmt.Update);
    }

    [Fact]
    public void ParsesBreakAndContinueStatements()
    {
        var program = JsParser.ParseScript(new SourceText("for(;;){ continue; break; }"));
        var forStmt = Assert.IsType<ForStatementNode>(Assert.Single(program.Body));
        var body = Assert.IsType<BlockStatementNode>(forStmt.Body);
        Assert.IsType<ContinueStatementNode>(body.Statements[0]);
        Assert.IsType<BreakStatementNode>(body.Statements[1]);
    }

    [Fact]
    public void ParsesFunctionExpressionAndCall()
    {
        var program = JsParser.ParseScript(new SourceText("let r = (function(a){ return a + 1; })(2); r;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var call = Assert.IsType<CallExpressionNode>(decl.Declarators[0].Initializer);
        var paren = Assert.IsType<ParenthesizedExpressionNode>(call.Callee);
        Assert.IsType<FunctionExpressionNode>(paren.Expression);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void ParsesNewExpression()
    {
        var program = JsParser.ParseScript(new SourceText("let x = new Foo(1,2); x;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var ne = Assert.IsType<NewExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal(2, ne.Arguments.Count);
    }

    [Fact]
    public void ParsesTypeofUnaryExpression()
    {
        var program = JsParser.ParseScript(new SourceText("let t = typeof 1; t;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var unary = Assert.IsType<UnaryExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal("typeof", unary.Operator);
    }

    [Fact]
    public void ParsesHexOctalBinaryNumericLiterals()
    {
        var program = JsParser.ParseScript(new SourceText("0x2A; 0o10; 0b11;"));
        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var n1 = Assert.IsType<NumericLiteralExpressionNode>(s1.Expression);
        Assert.Equal(42, n1.Value);
        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        var n2 = Assert.IsType<NumericLiteralExpressionNode>(s2.Expression);
        Assert.Equal(8, n2.Value);
        var s3 = Assert.IsType<ExpressionStatementNode>(program.Body[2]);
        var n3 = Assert.IsType<NumericLiteralExpressionNode>(s3.Expression);
        Assert.Equal(3, n3.Value);
    }

    [Fact]
    public void ParsesRegexLiteralExpression()
    {
        var program = JsParser.ParseScript(new SourceText("let r = /abc/i; r;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        Assert.IsType<RegexLiteralExpressionNode>(decl.Declarators[0].Initializer);
    }

    [Fact]
    public void ParsesCompoundAssignment()
    {
        var program = JsParser.ParseScript(new SourceText("let x = 1; x += 2;"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        var assign = Assert.IsType<AssignmentExpressionNode>(stmt.Expression);
        Assert.IsType<BinaryExpressionNode>(assign.Right);
    }

    [Fact]
    public void ParsesUnaryPlusExpression()
    {
        var program = JsParser.ParseScript(new SourceText("let x = +1; x;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var unary = Assert.IsType<UnaryExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal("+", unary.Operator);
    }

    [Fact]
    public void ParsesObjectMethodShorthand()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { add(a,b) { return a + b; } };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.IsType<FunctionExpressionNode>(obj.Properties[0].Value);
    }

    [Fact]
    public void ParsesStrictEqualityOperators()
    {
        var program = JsParser.ParseScript(new SourceText("1 === 1; 1 !== 2;"));
        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var b1 = Assert.IsType<BinaryExpressionNode>(s1.Expression);
        Assert.Equal("===", b1.Operator);
        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        var b2 = Assert.IsType<BinaryExpressionNode>(s2.Expression);
        Assert.Equal("!==", b2.Operator);
    }
}
