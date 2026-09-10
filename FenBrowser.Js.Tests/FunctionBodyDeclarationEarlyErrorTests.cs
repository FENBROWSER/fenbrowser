using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 15.2.1 FunctionBody and 15.7.1 ClassStaticBlockBody early errors:
/// only the body's top-level let, const and class are lexical, and a top-level
/// function declaration is a var. So a var, a repeated function or a parameter
/// may share a function's name, while a let may share none of them. Every
/// expected value here is what V8 reports for the same source.
/// </summary>
public sealed class FunctionBodyDeclarationEarlyErrorTests
{
    private static string Outcome(string source)
    {
        var escaped = source.Replace("\\", "\\\\").Replace("'", "\\'");
        var program = "var outcome; try { eval('" + escaped + "'); outcome = 'ok'; } catch (e) { outcome = e.constructor.name; } outcome;";
        var function = new BytecodeCompiler().CompileScript(new SourceText(program));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Theory]
    // test262 language/function-code/S10.2.1_A4_T2.js
    [InlineData("function g() { var x; function x() {} }")]
    [InlineData("'use strict'; function g() { var x; function x() {} }")]
    [InlineData("'use strict'; function g() { function x() {} function x() {} }")]
    [InlineData("(() => { var x; function x() {} })")]
    [InlineData("({ m() { var x; function x() {} } })")]
    [InlineData("(class { m() { var x; function x() {} } })")]
    [InlineData("(class { static { var x; function x() {} } })")]
    [InlineData("(class { static { function x() {} function x() {} } })")]
    [InlineData("function f(x) { function x() {} }")]
    [InlineData("({ m(x) { function x() {} } })")]
    [InlineData("(class { m(x) { function x() {} } })")]
    [InlineData("((x) => { function x() {} })")]
    public void AFunctionBodysFunctionIsAVar(string source)
    {
        Assert.Equal("ok", Outcome(source));
    }

    [Theory]
    [InlineData("function g() { let x; function x() {} }")]
    [InlineData("function g() { function x() {} let x; }")]
    [InlineData("(class { static { let x; function x() {} } })")]
    [InlineData("function f(x) { let x; }")]
    [InlineData("function f() { let x; class x {} }")]
    [InlineData("function f() { let x; { var x; } }")]
    [InlineData("'use strict'; { function x() {} function x() {} }")]
    public void ALetStillMeetsEveryOtherDeclaration(string source)
    {
        Assert.Equal("SyntaxError", Outcome(source));
    }
}
