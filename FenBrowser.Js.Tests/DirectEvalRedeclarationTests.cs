using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 19.2.1.3 EvalDeclarationInstantiation checks a direct eval's var
/// and function names against the scopes between its lexEnv and the caller's
/// varEnv, and never against the varEnv itself. So an eval may redeclare a var,
/// a function or a parameter the caller already has - but not the caller's
/// function-level let or const, which live in a lexical environment below the
/// varEnv. Every expected value here is what V8 returns.
/// </summary>
public sealed class DirectEvalRedeclarationTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AnEvalMayRedeclareTheCallersVar()
    {
        // test262 language/eval-code/direct/var-env-var-init-local-exstng.js
        Assert.Equal(
            "44443:44443",
            RunString("function f() { var x = 44443; var initial; eval('initial = x; var x;'); return initial + ':' + x; } f();"));
    }

    [Fact]
    public void AnEvalMayRedeclareTheCallersVarAsAFunction()
    {
        // test262 language/eval-code/direct/var-env-func-init-local-update.js
        Assert.Equal(
            "function:function",
            RunString(
                "function f() { var f2 = 88; var initial;" +
                "  eval('initial = typeof f2; function f2() { return 33; }'); return initial + ':' + typeof f2; } f();"));
    }

    [Fact]
    public void AnEvalMayRedeclareTheCallersFunctionAsAVar()
    {
        Assert.Equal(
            "number:2",
            RunString("function f() { function h() { return 1; } eval('var h = 2;'); return typeof h + ':' + h; } f();"));
    }

    [Fact]
    public void AnEvalMayRedeclareAParameter()
    {
        Assert.Equal("5", RunString("function f(p) { eval('var p = 5;'); return String(p); } f(1);"));
    }

    [Fact]
    public void AnEvalMayNotRedeclareTheCallersFunctionLevelLet()
    {
        Assert.Equal(
            "SyntaxError",
            RunString(
                "function f() { let y = 1;" +
                "  try { eval('var y;'); return 'no-throw'; } catch (e) { return e.constructor.name; } } f();"));
    }

    [Fact]
    public void AnEvalMayNotRedeclareTheCallersFunctionLevelConst()
    {
        Assert.Equal(
            "SyntaxError",
            RunString(
                "function f() { const k = 1;" +
                "  try { eval('var k;'); return 'no-throw'; } catch (e) { return e.constructor.name; } } f();"));
    }
}
