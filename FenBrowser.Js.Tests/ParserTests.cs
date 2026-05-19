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
    public void ParsesClassDeclarationWithExtends()
    {
        var program = JsParser.ParseScript(new SourceText("class A extends B { constructor() {} }"));
        var cls = Assert.IsType<ClassDeclarationNode>(program.Body[0]);
        Assert.Equal("A", cls.Name);
        Assert.NotNull(cls.BaseClass);
    }

    [Fact]
    public void ParsesLabeledFunctionDeclarationInStatementPosition()
    {
        var program = JsParser.ParseScript(new SourceText("label: function f() {}"));
        var labeled = Assert.IsType<LabeledStatementNode>(program.Body[0]);
        Assert.Equal("label", labeled.Label);
        Assert.IsType<FunctionDeclarationNode>(labeled.Body);
    }

    [Fact]
    public void ParsesFunctionNamedAsyncInNonStrictSubset()
    {
        var program = JsParser.ParseScript(new SourceText("function async() {}"));
        var fn = Assert.IsType<FunctionDeclarationNode>(program.Body[0]);
        Assert.Equal("async", fn.Name);
    }

    [Fact]
    public void ParsesClassExpressionInNewExpression()
    {
        var program = JsParser.ParseScript(new SourceText("new class extends B {}();"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var ne = Assert.IsType<NewExpressionNode>(stmt.Expression);
        Assert.IsType<ClassExpressionNode>(ne.Callee);
    }

    [Fact]
    public void ParsesSwitchStatement()
    {
        var program = JsParser.ParseScript(new SourceText("switch (x) { case 1: y = 2; break; default: y = 3; }"));
        var sw = Assert.IsType<SwitchStatementNode>(program.Body[0]);
        Assert.Equal(2, sw.Cases.Count);
        Assert.NotNull(sw.Cases[0].Test);
        Assert.Null(sw.Cases[1].Test);
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
    public void ParsesVariableDeclarationWithArrayBindingPatternSubset()
    {
        var program = JsParser.ParseScript(new SourceText("const [a] = arr;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        Assert.StartsWith("__pattern", decl.Declarators[0].Identifier);
        Assert.NotNull(decl.Declarators[0].Initializer);
    }

    [Fact]
    public void ParsesVariableDeclarationWithObjectBindingPatternSubset()
    {
        var program = JsParser.ParseScript(new SourceText("const {x} = obj;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        Assert.StartsWith("__pattern", decl.Declarators[0].Identifier);
        Assert.NotNull(decl.Declarators[0].Initializer);
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
    public void ParsesEmptyStatement()
    {
        var program = JsParser.ParseScript(new SourceText(";"));
        Assert.IsType<EmptyStatementNode>(Assert.Single(program.Body));
    }

    [Fact]
    public void ParsesIfWithFunctionDeclarationElseEmptyStatement()
    {
        var program = JsParser.ParseScript(new SourceText("if (true) function f() {} else ;"));
        var ifStmt = Assert.IsType<IfStatementNode>(Assert.Single(program.Body));
        Assert.IsType<FunctionDeclarationNode>(ifStmt.Consequent);
        Assert.IsType<EmptyStatementNode>(ifStmt.Alternate);
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
    public void ParsesArrowFunctionWithDefaultParameter()
    {
        var program = JsParser.ParseScript(new SourceText("const f = (x = 1) => x;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var arrow = Assert.IsType<ArrowFunctionExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(arrow.Parameters);
        Assert.Equal("x", arrow.Parameters[0]);
    }

    [Fact]
    public void ParsesAsyncArrowFunctionExpressionBody()
    {
        var program = JsParser.ParseScript(new SourceText("const f = async () => 1;"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var arrow = Assert.IsType<ArrowFunctionExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Empty(arrow.Parameters);
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
    public void ParsesTryCatchWithPatternParameter()
    {
        var program = JsParser.ParseScript(new SourceText("try { throw {}; } catch ({ f }) { f; }"));
        var tc = Assert.IsType<TryCatchStatementNode>(Assert.Single(program.Body));
        Assert.Equal("<pattern>", tc.CatchIdentifier);
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
    public void ParsesAsyncFunctionExpressionInCallArguments()
    {
        var program = JsParser.ParseScript(new SourceText("asyncTest(async function () { return 1; });"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var call = Assert.IsType<CallExpressionNode>(stmt.Expression);
        Assert.Single(call.Arguments);
        Assert.IsType<FunctionExpressionNode>(call.Arguments[0]);
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
    public void ParsesBigIntSuffixedNumericLiteralsAsNumericSubset()
    {
        var program = JsParser.ParseScript(new SourceText("1n; 0x2An; 0o10n; 0b11n;"));
        Assert.Equal(4, program.Body.Count);
        foreach (var statement in program.Body)
        {
            var expr = Assert.IsType<ExpressionStatementNode>(statement);
            Assert.IsType<NumericLiteralExpressionNode>(expr.Expression);
        }
    }

    [Fact]
    public void ParsesDecimalFractionAndExponentNumericLiterals()
    {
        var program = JsParser.ParseScript(new SourceText("50.999999; 1e3; 1E-2;"));
        Assert.Equal(3, program.Body.Count);

        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var n1 = Assert.IsType<NumericLiteralExpressionNode>(s1.Expression);
        Assert.Equal(50.999999, n1.Value);

        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        var n2 = Assert.IsType<NumericLiteralExpressionNode>(s2.Expression);
        Assert.Equal(1000, n2.Value);

        var s3 = Assert.IsType<ExpressionStatementNode>(program.Body[2]);
        var n3 = Assert.IsType<NumericLiteralExpressionNode>(s3.Expression);
        Assert.Equal(0.01, n3.Value);
    }

    [Fact]
    public void ParsesModuloBinaryExpression()
    {
        var program = JsParser.ParseScript(new SourceText("10 % 3;"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var bin = Assert.IsType<BinaryExpressionNode>(stmt.Expression);
        Assert.Equal("%", bin.Operator);
    }

    [Fact]
    public void ParsesCoalesceAndLogicalAssignmentOperators()
    {
        var program = JsParser.ParseScript(new SourceText("a ?? b; x &&= y; x ||= z; x ??= q;"));
        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var b1 = Assert.IsType<BinaryExpressionNode>(s1.Expression);
        Assert.Equal("??", b1.Operator);

        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        Assert.IsType<AssignmentExpressionNode>(s2.Expression);
        var s3 = Assert.IsType<ExpressionStatementNode>(program.Body[2]);
        Assert.IsType<AssignmentExpressionNode>(s3.Expression);
        var s4 = Assert.IsType<ExpressionStatementNode>(program.Body[3]);
        Assert.IsType<AssignmentExpressionNode>(s4.Expression);
    }

    [Fact]
    public void ParsesInAndInstanceofBinaryOperators()
    {
        var program = JsParser.ParseScript(new SourceText("x in y; a instanceof B;"));
        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var b1 = Assert.IsType<BinaryExpressionNode>(s1.Expression);
        Assert.Equal("in", b1.Operator);
        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        var b2 = Assert.IsType<BinaryExpressionNode>(s2.Expression);
        Assert.Equal("instanceof", b2.Operator);
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
    public void ParsesObjectLiteralComputedPropertyKey()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { [\"a\" + \"b\"]: 1 };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.True(obj.Properties[0].IsComputed);
        Assert.NotNull(obj.Properties[0].ComputedKey);
        Assert.Null(obj.Properties[0].Key);
    }

    [Fact]
    public void ParsesObjectLiteralComputedMethodShorthand()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { [\"add\"](a,b) { return a + b; } };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.True(obj.Properties[0].IsComputed);
        Assert.NotNull(obj.Properties[0].ComputedKey);
        Assert.IsType<FunctionExpressionNode>(obj.Properties[0].Value);
    }

    [Fact]
    public void ParsesObjectPatternStyleDefaultInAssignmentTarget()
    {
        var program = JsParser.ParseScript(new SourceText("({x = counter()} = {x: v});"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var outerParen = Assert.IsType<ParenthesizedExpressionNode>(stmt.Expression);
        var assign = Assert.IsType<AssignmentExpressionNode>(outerParen.Expression);
        var obj = assign.Left switch
        {
            ObjectLiteralExpressionNode o => o,
            ParenthesizedExpressionNode p => Assert.IsType<ObjectLiteralExpressionNode>(p.Expression),
            _ => throw new InvalidOperationException($"Unexpected assignment target node: {assign.Left.GetType().Name}")
        };
        Assert.Single(obj.Properties);
        Assert.Equal("x", obj.Properties[0].Key);
    }

    [Fact]
    public void ParsesObjectLiteralKeywordPropertyKey()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { return: 1, throw: 2 };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal(2, obj.Properties.Count);
    }

    [Fact]
    public void ParsesObjectLiteralGetterWithComputedName()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { get [Symbol.iterator]() { return this; } };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.True(obj.Properties[0].IsComputed);
        Assert.IsType<FunctionExpressionNode>(obj.Properties[0].Value);
    }

    [Fact]
    public void ParsesObjectLiteralGetterWithIdentifierName()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { get maxByteLength() { return 1; } };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.Equal("maxByteLength", obj.Properties[0].Key);
    }

    [Fact]
    public void ParsesObjectLiteralNumericPropertyKey()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { 0: 1, 1: 2 };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal("0", obj.Properties[0].Key);
        Assert.Equal("1", obj.Properties[1].Key);
    }

    [Fact]
    public void ParsesObjectLiteralAsyncMethodShorthand()
    {
        var program = JsParser.ParseScript(new SourceText("let o = { async next() { return 1; } };"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var obj = Assert.IsType<ObjectLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Single(obj.Properties);
        Assert.Equal("next", obj.Properties[0].Key);
        Assert.IsType<FunctionExpressionNode>(obj.Properties[0].Value);
    }

    [Fact]
    public void ParsesDotMemberWithKeywordPropertyName()
    {
        var program = JsParser.ParseScript(new SourceText("obj.return(1);"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var call = Assert.IsType<CallExpressionNode>(stmt.Expression);
        var member = Assert.IsType<MemberExpressionNode>(call.Callee);
        Assert.Equal("return", member.Property);
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

    [Fact]
    public void ParsesAnnexBHtmlOpenCommentForms()
    {
        var program = JsParser.ParseScript(new SourceText("let x = 1; <!--comment\nx = x + 1; x;"));
        Assert.Equal(3, program.Body.Count);
    }

    [Fact]
    public void ParsesUnicodeLineSeparatorWithHtmlCloseComment()
    {
        var source = "let x = 0;\u2028-->comment\nx = 1; x;";
        var program = JsParser.ParseScript(new SourceText(source));
        Assert.Equal(3, program.Body.Count);
    }

    [Fact]
    public void ParsesPrefixAndPostfixUpdateExpressions()
    {
        var program = JsParser.ParseScript(new SourceText("let x = 0; x++; ++x; x--; --x;"));
        Assert.Equal(5, program.Body.Count);
        Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        Assert.IsType<ExpressionStatementNode>(program.Body[2]);
        Assert.IsType<ExpressionStatementNode>(program.Body[3]);
        Assert.IsType<ExpressionStatementNode>(program.Body[4]);
    }

    [Fact]
    public void ParsesUpdateExpressionsOnCallTargetsInParserSubset()
    {
        var program = JsParser.ParseScript(new SourceText("f()++; ++f();"));
        Assert.Equal(2, program.Body.Count);
        var s1 = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        Assert.IsType<AssignmentExpressionNode>(s1.Expression);
        var s2 = Assert.IsType<ExpressionStatementNode>(program.Body[1]);
        Assert.IsType<AssignmentExpressionNode>(s2.Expression);
    }

    [Fact]
    public void ParsesArraySpreadElements()
    {
        var program = JsParser.ParseScript(new SourceText("let x = [1, ...arr, 3];"));
        var decl = Assert.IsType<VariableDeclarationStatementNode>(program.Body[0]);
        var arr = Assert.IsType<ArrayLiteralExpressionNode>(decl.Declarators[0].Initializer);
        Assert.Equal(3, arr.Elements.Count);
        Assert.IsType<SpreadElementExpressionNode>(arr.Elements[1]);
    }

    [Fact]
    public void ParsesCommaExpressionInsideComputedMember()
    {
        var program = JsParser.ParseScript(new SourceText("obj[a, b];"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var member = Assert.IsType<MemberExpressionNode>(stmt.Expression);
        var seq = Assert.IsType<BinaryExpressionNode>(member.PropertyExpression);
        Assert.Equal(",", seq.Operator);
    }

    [Fact]
    public void ParsesForOfStatement()
    {
        var program = JsParser.ParseScript(new SourceText("for (let x of arr) { x; }"));
        Assert.IsType<ForOfStatementNode>(Assert.Single(program.Body));
    }

    [Fact]
    public void ParsesForInStatement()
    {
        var program = JsParser.ParseScript(new SourceText("for (let k in obj) { k; }"));
        Assert.IsType<ForInStatementNode>(Assert.Single(program.Body));
    }

    [Fact]
    public void ParsesForAwaitOfStatementSubset()
    {
        var program = JsParser.ParseScript(new SourceText("for await (var x of iter) { x; }"));
        Assert.IsType<ForOfStatementNode>(Assert.Single(program.Body));
    }

    [Fact]
    public void ParsesForWithDeclarationAndEmptyTestAndUpdate()
    {
        var program = JsParser.ParseScript(new SourceText("for (let f; ; ) { break; }"));
        var forStmt = Assert.IsType<ForStatementNode>(Assert.Single(program.Body));
        Assert.NotNull(forStmt.Initializer);
        Assert.Null(forStmt.Test);
        Assert.Null(forStmt.Update);
    }

    [Fact]
    public void ParsesForHeaderWithInitializerThatContainsInOperatorSubset()
    {
        var program = JsParser.ParseScript(new SourceText("for (a = 0 in {});"));
        var forStmt = Assert.IsType<ForStatementNode>(Assert.Single(program.Body));
        Assert.NotNull(forStmt.Initializer);
        Assert.Null(forStmt.Test);
        Assert.Null(forStmt.Update);
    }

    [Fact]
    public void ParsesForHeaderWithDeclarationInitializerContainingInOperatorSubset()
    {
        var program = JsParser.ParseScript(new SourceText("for (let a = 0 in {});"));
        var forStmt = Assert.IsType<ForStatementNode>(Assert.Single(program.Body));
        Assert.NotNull(forStmt.Initializer);
        Assert.Null(forStmt.Test);
        Assert.Null(forStmt.Update);
    }

    [Fact]
    public void ParsesThisExpression()
    {
        var program = JsParser.ParseScript(new SourceText("this.x;"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var member = Assert.IsType<MemberExpressionNode>(stmt.Expression);
        Assert.IsType<ThisExpressionNode>(member.Object);
    }

    [Fact]
    public void ParsesTemplateLiteralAsStringSubset()
    {
        var program = JsParser.ParseScript(new SourceText("`hello ${name}`;"));
        var stmt = Assert.IsType<ExpressionStatementNode>(program.Body[0]);
        var str = Assert.IsType<StringLiteralExpressionNode>(stmt.Expression);
        Assert.Equal("hello ${name}", str.Value);
    }

    [Fact]
    public void ParsesGeneratorFunctionDeclarationSubset()
    {
        var program = JsParser.ParseScript(new SourceText("function* g(){ yield 1; }"));
        var fn = Assert.IsType<FunctionDeclarationNode>(program.Body[0]);
        Assert.Equal("g", fn.Name);
    }

    [Fact]
    public void ParsesAsyncGeneratorFunctionDeclarationSubset()
    {
        var program = JsParser.ParseScript(new SourceText("async function* g(){ yield 1; }"));
        var fn = Assert.IsType<FunctionDeclarationNode>(program.Body[0]);
        Assert.Equal("g", fn.Name);
    }

    [Fact]
    public void ParsesYieldStarExpressionInGeneratorSubset()
    {
        var program = JsParser.ParseScript(new SourceText("function* g(){ yield* iter; }"));
        var fn = Assert.IsType<FunctionDeclarationNode>(program.Body[0]);
        var exprStmt = Assert.IsType<ExpressionStatementNode>(fn.Body.Statements[0]);
        var unary = Assert.IsType<UnaryExpressionNode>(exprStmt.Expression);
        Assert.Equal("yield*", unary.Operator);
    }

    [Fact]
    public void ParsesFunctionParametersWithDefaultAndRestSubset()
    {
        var program = JsParser.ParseScript(new SourceText("function f(a = 1, ...rest) { return a; }"));
        var fn = Assert.IsType<FunctionDeclarationNode>(program.Body[0]);
        Assert.Equal(new[] { "a", "rest" }, fn.Parameters);
    }
}
