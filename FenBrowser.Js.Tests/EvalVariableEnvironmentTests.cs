using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 19.2.1.1 PerformEval: every eval runs in a fresh lexical
/// environment, so its let, const and class stay inside it; its var and
/// function declarations go to the caller's variable environment - out of any
/// block or catch the call sits in, but not out of an arrow - or, for a strict
/// eval, stay in the fresh environment. Every expected value here is what V8
/// returns, or what the test262 case named beside it asserts.
/// </summary>
public sealed class EvalVariableEnvironmentTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AnEvalLetDoesNotCollideWithTheCallersLet()
    {
        // test262 language/eval-code/direct/lex-env-distinct-let.js
        Assert.Equal("23", RunString("function f() { let outside = 23; eval('let outside = 1;'); return String(outside); } f();"));
    }

    [Fact]
    public void AnEvalLetDoesNotLeak()
    {
        Assert.Equal("undefined", RunString("function f() { eval('let inner = 3;'); return typeof inner; } f();"));
    }

    [Fact]
    public void AnEvalConstDoesNotLeak()
    {
        Assert.Equal("undefined", RunString("function f() { eval('const innerC = 3;'); return typeof innerC; } f();"));
    }

    [Fact]
    public void AnEvalClassDoesNotLeak()
    {
        Assert.Equal("undefined", RunString("function f() { eval('class InnerK {}'); return typeof InnerK; } f();"));
    }

    [Fact]
    public void AnEvalLetSeesTheCallersVar()
    {
        Assert.Equal("8", RunString("function f() { var cv = 7; return String(eval('let local = cv + 1; local')); } f();"));
    }

    [Fact]
    public void AnEvalLetShadowsWithoutTouchingTheCaller()
    {
        Assert.Equal(
            "inner:outer",
            RunString("function f() { var sh = 'outer'; var r = eval('let sh = \"inner\"; sh'); return r + ':' + sh; } f();"));
    }

    [Fact]
    public void AnEvalVarHoistsOutOfABlock()
    {
        Assert.Equal(
            "number:4",
            RunString("function f() { { let q = 1; { eval('var outOfBlock = 4;'); } } return typeof outOfBlock + ':' + outOfBlock; } f();"));
    }

    [Fact]
    public void AnEvalFunctionHoistsOutOfABlock()
    {
        Assert.Equal(
            "function:6",
            RunString("function f() { { eval('function fromBlock() { return 6; }'); } return typeof fromBlock + ':' + fromBlock(); } f();"));
    }

    [Fact]
    public void AnEvalVarIsVisibleInsideTheBlockToo()
    {
        Assert.Equal("5:5", RunString("function f() { { eval('var seen = 5;'); var inside = seen; } return inside + ':' + seen; } f();"));
    }

    [Fact]
    public void AnEvalVarInACatchAssignsTheParameterAndHoistsTheRest()
    {
        // Annex B.3.4: `var e` shares the parameter's name, so its initializer
        // assigns the parameter; `sibling` is an ordinary var of the function.
        Assert.Equal(
            "undefined:8",
            RunString("function f() { try { throw 1; } catch (e) { eval('var e = 2; var sibling = 8;'); } return typeof e + ':' + sibling; } f();"));
    }

    [Fact]
    public void AnEvalVarInACatchLeavesAnUndefinedFunctionScopedVar()
    {
        Assert.Equal(
            "undefined:undefined",
            RunString("function f() { try { throw 1; } catch (e) { eval('var e = 2'); } return typeof e + ':' + String(e); } f();"));
    }

    [Fact]
    public void AnEvalVarStaysInsideAnArrow()
    {
        Assert.Equal(
            "number:undefined",
            RunString("function f() { var r = (() => { eval('var arrowLocal = 1'); return typeof arrowLocal; })(); return r + ':' + typeof arrowLocal; } f();"));
    }

    [Fact]
    public void AnEvalFunctionStaysInsideAnArrow()
    {
        Assert.Equal(
            "function:undefined",
            RunString("function f() { var r = (() => { eval('function inArrow() {}'); return typeof inArrow; })(); return r + ':' + typeof inArrow; } f();"));
    }

    [Fact]
    public void ANestedSloppyEvalHoists()
    {
        Assert.Equal(
            "number:3",
            RunString("function f() { eval('eval(\"var deep = 3\")'); return typeof deep + ':' + deep; } f();"));
    }

    [Fact]
    public void AStrictCallerKeepsEvalVarsLocal()
    {
        Assert.Equal("undefined", RunString("function f() { 'use strict'; eval('var s2 = 1'); return typeof s2; } f();"));
    }

    [Fact]
    public void AnIndirectSloppyEvalLetDoesNotLeakOntoTheGlobal()
    {
        // test262 language/eval-code/indirect/lex-env-distinct-let.js
        Assert.Equal("undefined", RunString("(0, eval)('let globalLeak = 1;'); typeof globalLeak;"));
    }

    [Fact]
    public void ADirectSloppyEvalVarOverAGlobalLetIsStillASyntaxError()
    {
        // test262 language/eval-code/direct/var-env-global-lex-non-strict.js
        Assert.Equal(
            "SyntaxError",
            RunString("let gx; var outcome; try { eval('var gx;'); outcome = 'no-throw'; } catch (e) { outcome = e.constructor.name; } outcome;"));
    }

    [Fact]
    public void AnIndirectSloppyEvalVarOverAGlobalLetIsStillASyntaxError()
    {
        // test262 language/eval-code/indirect/var-env-global-lex-non-strict.js
        Assert.Equal(
            "SyntaxError",
            RunString("let gy; var outcome; try { (0, eval)('var gy;'); outcome = 'no-throw'; } catch (e) { outcome = e.constructor.name; } outcome;"));
    }
}
