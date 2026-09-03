using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 14.7.4.9 CreatePerIterationEnvironment — a `let` head binds a fresh
/// copy of its variables on every turn of the loop, so a function made on one
/// turn keeps that turn's values.
/// </summary>
public sealed class ForLetPerIterationTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void FunctionExpressionCapturesItsOwnIteration()
    {
        Assert.Equal(
            "0,1,2",
            Run("var f = []; for (let i = 0; i < 3; i++) { f.push(function () { return i; }); }" +
                "f[0]() + ',' + f[1]() + ',' + f[2]();").AsString());
    }

    [Fact]
    public void ArrowFunctionCapturesItsOwnIteration()
    {
        Assert.Equal(
            "0,1,2",
            Run("function g() { var f = []; for (let i = 0; i < 3; i++) { f.push(() => i); }" +
                "return f[0]() + ',' + f[1]() + ',' + f[2](); } g();").AsString());
    }

    [Fact]
    public void EveryHeadNameIsCopied()
    {
        Assert.Equal(
            "0:9,1:8,2:7",
            Run("var f = []; for (let i = 0, j = 9; i < 3; i++, j--) { f.push(function () { return i + ':' + j; }); }" +
                "f[0]() + ',' + f[1]() + ',' + f[2]();").AsString());
    }

    [Fact]
    public void ContinueStillCopies()
    {
        Assert.Equal(
            "0,2",
            Run("var f = []; for (let i = 0; i < 3; i++) { if (i === 1) continue; f.push(function () { return i; }); }" +
                "f[0]() + ',' + f[1]();").AsString());
    }

    [Fact]
    public void BodyWriteReachesOnlyTheIterationThatMadeIt()
    {
        // A write the body undoes before the turn ends leaves the function on the
        // head's value.
        Assert.Equal(
            "0,1,2",
            Run("var f = []; for (let i = 0; i < 3; i++) { i += 100; f.push(function () { return i; }); i -= 100; }" +
                "f[0]() + ',' + f[1]() + ',' + f[2]();").AsString());

        // One that survives the turn is copied forward into the head's update and
        // stays visible to the function made on that turn.
        Assert.Equal(
            "1:10",
            Run("var f = []; for (let i = 0; i < 3; i++) { f.push(function () { return i; }); i += 10; }" +
                "f.length + ':' + f[0]();").AsString());
    }

    [Fact]
    public void VarHeadKeepsOneBinding()
    {
        Assert.Equal(
            "3,3,3",
            Run("var f = []; for (var i = 0; i < 3; i++) { f.push(function () { return i; }); }" +
                "f[0]() + ',' + f[1]() + ',' + f[2]();").AsString());
    }

    [Fact]
    public void ConstHeadStillRunsAndBreaksOut()
    {
        Assert.Equal(1d, Run("var r = 0; for (let i = 0; i < 5; i++) { if (i === 1) { r = i; break; } } r;").AsNumber());
        Assert.Equal(5d, Run("var n = 0; for (const c = 5; n < 1; n++) { } 5;").AsNumber());
    }

    [Fact]
    public void ClosuresSeeLaterWritesToTheirOwnIteration()
    {
        // test262 language/statements/for/scope-body-lex-boundary.js
        Assert.Equal(
            "first,second",
            Run("var pf, ps = null; for (let x = 'first'; ps === null; x = 'second') " +
                "{ if (!pf) { pf = function () { return x; }; } else { ps = function () { return x; }; } }" +
                "pf() + ',' + ps();").AsString());
    }
}
