using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 13.16: Expression : Expression , AssignmentExpression, and an
/// ArrowFunction is an AssignmentExpression - so an arrow is legal as a
/// non-final operand of a comma expression. The parser used to return straight
/// out of the arrow branch, so any such sequence was a parse error. Minified
/// bundles emit this constantly; it rejected a whole react.dev chunk.
/// </summary>
public class ArrowSequenceExpressionTests
{
    private static double RunNumber(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void BlockBodiedArrowIsLegalBeforeCommaOperator()
    {
        Assert.Equal(2d, RunNumber("var x = ((a, b) => { return 1; }, 2); x;"));
    }

    [Fact]
    public void ConciseArrowIsLegalBeforeCommaOperator()
    {
        Assert.Equal(2d, RunNumber("var a = (() => 1, 2); a;"));
    }

    [Fact]
    public void ChainedArrowsInCommaSequenceEvaluateToLastOperand()
    {
        Assert.Equal(3d, RunNumber("var v = ((x) => {}, (y) => {}, 3); v;"));
    }

    [Fact]
    public void ArrowBeforeCommaIsLegalInForInitializer()
    {
        Assert.Equal(1d, RunNumber("for (var i = ((x) => x, 0); i < 1; i++); i;"));
    }

    // The react.dev shape: a destructuring assignment whose right-hand side is a
    // parenthesised sequence starting with an arrow.
    [Fact]
    public void DestructuringFromParenthesisedArrowSequence()
    {
        Assert.Equal("second", RunString(
            "var first, second;" +
            "[first, second] = ((e, t) => { return e; }, ['a', 'second']);" +
            "second;"));
    }

    [Fact]
    public void CommaSequenceStillEvaluatesEveryOperandInOrder()
    {
        Assert.Equal(12d, RunNumber(
            "var log = 0;" +
            "var r = ((a) => { return a; }, log = log + 5, log = log + 7, log);" +
            "r;"));
    }

    // Guards: contexts parsed at binding power 2 must keep treating the comma as
    // their own separator, not as the comma operator.
    [Fact]
    public void ArrowInitializerDoesNotSwallowDeclaratorSeparator()
    {
        Assert.Equal(7d, RunNumber("var f = () => {}, g = 7; g;"));
    }

    [Fact]
    public void ArrowArgumentDoesNotSwallowArgumentSeparator()
    {
        Assert.Equal(7d, RunNumber("function h(a, b) { return b; } h(x => x, 7);"));
    }

    [Fact]
    public void ArrowElementDoesNotSwallowArraySeparator()
    {
        Assert.Equal(2d, RunNumber("[(x) => x, 1].length;"));
    }

    [Fact]
    public void ArrowValueDoesNotSwallowObjectSeparator()
    {
        Assert.Equal(5d, RunNumber("var o = { m: (x) => x, n: 5 }; o.n;"));
    }

    [Fact]
    public void ConciseArrowBodyStillAbsorbsTrailingBinaryOperator()
    {
        // `x => x + 1` is the arrow's body, not `(x => x) + 1`.
        Assert.Equal(2d, RunNumber("var u = x => x + 1; u(1);"));
    }

    [Fact]
    public void ArrowAsFinalCommaOperandStillParses()
    {
        Assert.Equal("function", RunString("var t = (1, (x) => x); typeof t;"));
    }
}
