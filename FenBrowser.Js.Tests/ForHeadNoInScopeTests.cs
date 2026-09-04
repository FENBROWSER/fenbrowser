using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A for-head's initializer is parsed under [~In] so a bare `in` is recognised
/// as the for-in marker (ECMA-262 14.7.4). That restriction must not escape the
/// head: a FunctionBody carries no [In] parameter, and parentheses restore
/// [+In]. Both leaked, and both rejected real minified bundles.
/// </summary>
public class ForHeadNoInScopeTests
{
    private static double RunNumber(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void InOperatorInsideFunctionBodyInForInitializer()
    {
        Assert.True(RunBool(
            "for (var f = function () { return 'a' in { a: 1 }; }; false; ) {} f();"));
    }

    [Fact]
    public void InOperatorInsideFunctionBodyInEmptyForHead()
    {
        Assert.True(RunBool(
            "for (var f = function () { return 'a' in { a: 1 }; }; ; ) { break; } f();"));
    }

    [Fact]
    public void InOperatorInsideNestedFunctionBodyInForInitializer()
    {
        Assert.True(RunBool(
            "for (var f = function () { return function () { return 'a' in { a: 1 }; }; }; false; ) {} f()();"));
    }

    [Fact]
    public void ParenthesisedInOperatorIsAnOrdinaryForInitializer()
    {
        Assert.True(RunBool("var b = { a: 1 }; for (var x = ('a' in b); false; ) {} x;"));
    }

    [Fact]
    public void DoublyParenthesisedInOperatorIsAnOrdinaryForInitializer()
    {
        Assert.True(RunBool("var b = { a: 1 }; for (var x = (('a' in b)); false; ) {} x;"));
    }

    [Fact]
    public void ParenthesisedInInitializerStillRunsTheLoop()
    {
        Assert.Equal(3d, RunNumber(
            "var b = { a: 1 }; var n = 0;" +
            "for (var x = ('a' in b), i = 0; i < 3; i++) { n++; }" +
            "n;"));
    }

    // Annex B.3.5: `for (var x = init in obj)` stays a for-in, because there the
    // `in` really is at the top level of the head.
    [Fact]
    public void AnnexBLegacyForInInitializerStillParses()
    {
        Assert.Equal("xy", RunString("var r = ''; for (var a = 0 in { x: 1, y: 2 }) r += a; r;"));
    }

    [Fact]
    public void OrdinaryForInStillParses()
    {
        Assert.Equal("ab", RunString("var r = ''; for (var k in { a: 1, b: 2 }) r += k; r;"));
    }

    [Fact]
    public void OrdinaryForOfStillParses()
    {
        Assert.Equal(6d, RunNumber("var t = 0; for (var v of [1, 2, 3]) t += v; t;"));
    }

    [Fact]
    public void ThreePartForStillParses()
    {
        Assert.Equal(3d, RunNumber("for (var i = 0; i < 3; i++); i;"));
    }
}
