using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 14.7.5.6 ForIn/OfHeadEvaluation step 2: when a for-in, for-of or
/// for-await-of head declares let or const, the expression after `in` or `of`
/// runs with those names already bound and uninitialized. A reference to one -
/// directly or from a closure made there - is a ReferenceError, not a read of
/// the variable outside. Every expected value here is what V8 returns, or what
/// the test262 case named beside it asserts.
/// </summary>
public sealed class ForHeadTdzTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Theory]
    [InlineData("for (let x of [x]) {}")]
    [InlineData("for (let x in { a: x }) {}")]
    [InlineData("for (const [x, y] of [x]) {}")]
    public void TheExpressionSeesTheHeadsBindingUninitialized(string loop)
    {
        Assert.Equal(
            "ReferenceError",
            RunString("(function () { let x = 1; try { " + loop + " return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AClosureMadeInTheExpressionKeepsSeeingTheUninitializedBinding()
    {
        // test262 language/statements/for-in/scope-head-lex-open.js
        Assert.Equal(
            "ReferenceError",
            RunString(
                "(function () { let x = 'outside'; var p; for (let x of [p = function () { return typeof x; }]) {}" +
                " try { p(); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AnExpressionNotNamingTheHeadStillReadsTheOuterScope()
    {
        Assert.Equal("1", RunString("(function () { let x = 1; var seen; for (let y of [x]) { seen = y; } return String(seen); })()"));
    }

    [Fact]
    public void TheBodyStillSeesEachIterationsBinding()
    {
        Assert.Equal("p,q", RunString("(function () { let out = []; for (let k in { p: 1, q: 2 }) out.push(k); return out.join(','); })()"));
    }
}
