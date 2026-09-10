using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 19.2.1.3 EvalDeclarationInstantiation step 3.d: a sloppy direct
/// eval's var names are checked against the lexical bindings between the eval
/// and its caller's variable environment, and the walk stops there. A let
/// further out is shadowed, not in conflict. The variable environment's own
/// function still counts its let and const, and a parameter default sees only
/// the parameters. Every expected value here is what V8 returns, or what the
/// test262 case named beside it asserts. The try/catch sits in the scope under
/// test, never in a wrapper function, which would be the eval's variable
/// environment instead.
/// </summary>
public sealed class DirectEvalConflictWalkTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AVarShadowsALetOutsideTheCallingFunction()
    {
        Assert.Equal(
            "ok:1",
            RunString("(function o() { { let x = 1; (function () { eval('var x = 2'); })(); return 'ok:' + x; } })()"));
    }

    [Fact]
    public void AnArrowEvalVarShadowsTheEnclosingFunctionsLet()
    {
        Assert.Equal(
            "2:1",
            RunString("(function f() { let m = 1; return (() => { eval('var m = 2'); return m; })() + ':' + m; })()"));
    }

    [Fact]
    public void AnArrowEvalVarShadowsAnEnclosingBlocksLet()
    {
        Assert.Equal(
            "2:1",
            RunString("(function f() { { let b = 1; return (() => { eval('var b = 2'); return b; })() + ':' + b; } })()"));
    }

    [Fact]
    public void AnArrowEvalVarMeetsTheArrowsOwnLet()
    {
        Assert.Equal(
            "SyntaxError",
            RunString("(() => { let al = 1; try { eval('var al'); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AnArrowEvalVarMeetsALetInABlockInsideTheArrow()
    {
        Assert.Equal(
            "SyntaxError",
            RunString("(() => { let w = 1; { try { eval('var w'); return 'no-throw'; } catch (e) { return e.constructor.name; } } })()"));
    }

    [Fact]
    public void AnEvalInsideAnEvalStillMeetsTheFunctionsLet()
    {
        Assert.Equal(
            "SyntaxError",
            RunString("(function f() { let k = 1; try { eval(\"eval('var k')\"); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AnEvalInABlockRedeclaresTheFunctionsVar()
    {
        Assert.Equal("2", RunString("String((function f() { var v = 1; { eval('var v = 2'); } return v; })())"));
    }

    [Fact]
    public void AnArrowParameterEvalVarOverAParameterIsASyntaxError()
    {
        Assert.Equal(
            "SyntaxError",
            RunString("(function () { try { ((a, p = eval('var a = 2')) => a)(1); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }
}
